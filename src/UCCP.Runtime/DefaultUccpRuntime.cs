using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UCCP.Core.Contracts.Context;
using UCCP.Core.Contracts.Descriptors;
using UCCP.Core.Contracts.Envelopes;
using UCCP.Core.Contracts.Execution;
using UCCP.Core.Contracts.Security;
using UCCP.Runtime.Registry;

namespace UCCP.Runtime
{
    public class DefaultUccpRuntime : IUccpRuntime
    {
        private class IdempotencyRecord
        {
            public bool IsCompleted { get; set; }
            public UccpResponseEnvelope CachedResponse { get; set; }
            public DateTime ExpiresAtUtc { get; set; }
        }

        private readonly InMemoryCapabilityRegistry _registry;
        private readonly IContextEngine _contextEngine;
        private readonly IPolicyEngine _policyEngine;
        private readonly IAuditSink _auditSink;

        private readonly ConcurrentDictionary<string, IdempotencyRecord> _idempotencyStore =
            new ConcurrentDictionary<string, IdempotencyRecord>(StringComparer.Ordinal);

        public DefaultUccpRuntime(
            InMemoryCapabilityRegistry registry,
            IContextEngine contextEngine,
            IPolicyEngine policyEngine,
            IAuditSink auditSink = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _contextEngine = contextEngine ?? throw new ArgumentNullException(nameof(contextEngine));
            _policyEngine = policyEngine ?? throw new ArgumentNullException(nameof(policyEngine));
            _auditSink = auditSink;
        }

        public async Task<UccpResponseEnvelope> ExecuteAsync(
            UccpRequestEnvelope request,
            ClaimsPrincipal principal = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var sw = Stopwatch.StartNew();
            var response = new UccpResponseEnvelope
            {
                ProtocolVersion = request?.ProtocolVersion ?? "1.0",
                RequestId = request?.RequestId ?? Guid.NewGuid().ToString("D"),
                CorrelationId = request?.CorrelationId
            };

            if (request == null || string.IsNullOrWhiteSpace(request.CapabilityId))
            {
                response.Status = ExecutionStatus.Failed;
                response.Error = new UccpError
                {
                    Code = "UCCP_400_BAD_REQUEST",
                    Category = "VALIDATION",
                    Message = "Request envelope is null or missing capabilityId."
                };
                sw.Stop();
                response.ExecutionDurationMs = sw.Elapsed.TotalMilliseconds;
                await RecordAuditAsync(request, principal, response, sw.Elapsed.TotalMilliseconds).ConfigureAwait(false);
                return response;
            }

            // 1. Idempotency Key Check
            string idemKey = request.IdempotencyKey;
            if (!string.IsNullOrEmpty(idemKey))
            {
                if (_idempotencyStore.TryGetValue(idemKey, out var existingRecord))
                {
                    if (DateTime.UtcNow <= existingRecord.ExpiresAtUtc)
                    {
                        if (!existingRecord.IsCompleted)
                        {
                            response.Status = ExecutionStatus.Failed;
                            response.Error = new UccpError
                            {
                                Code = "UCCP_409_CONCURRENT_CONFLICT",
                                Category = "CONFLICT",
                                Message = $"Concurrent request in progress for idempotency key '{idemKey}'."
                            };
                            sw.Stop();
                            response.ExecutionDurationMs = sw.Elapsed.TotalMilliseconds;
                            return response;
                        }

                        // Return previously cached result immediately
                        var cached = existingRecord.CachedResponse;
                        sw.Stop();
                        return new UccpResponseEnvelope
                        {
                            ProtocolVersion = cached.ProtocolVersion,
                            RequestId = request.RequestId,
                            CorrelationId = request.CorrelationId,
                            Status = cached.Status,
                            ExecutionDurationMs = 0.0,
                            ContextHandle = cached.ContextHandle,
                            Summary = cached.Summary,
                            Data = cached.Data,
                            Error = cached.Error
                        };
                    }
                    _idempotencyStore.TryRemove(idemKey, out _);
                }

                // Register in-progress record
                _idempotencyStore[idemKey] = new IdempotencyRecord
                {
                    IsCompleted = false,
                    ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5)
                };
            }

            // 2. Resolve Capability
            var descriptor = await _registry.GetDescriptorAsync(request.CapabilityId).ConfigureAwait(false);
            if (descriptor == null)
            {
                response.Status = ExecutionStatus.Failed;
                response.Error = new UccpError
                {
                    Code = "UCCP_404_CAPABILITY_NOT_FOUND",
                    Category = "NOT_FOUND",
                    Message = $"Capability '{request.CapabilityId}' is not registered."
                };
                sw.Stop();
                response.ExecutionDurationMs = sw.Elapsed.TotalMilliseconds;
                await RecordAuditAsync(request, principal, response, sw.Elapsed.TotalMilliseconds).ConfigureAwait(false);
                return response;
            }

            // 3. Input Schema Validation (Fail-Fast)
            if (descriptor.InputSchema != null && descriptor.InputSchema["required"] is JArray requiredFields)
            {
                var payload = request.Payload ?? new JObject();
                foreach (var field in requiredFields)
                {
                    var fieldName = field.ToString();
                    if (payload[fieldName] == null)
                    {
                        response.Status = ExecutionStatus.Failed;
                        response.Error = new UccpError
                        {
                            Code = "UCCP_400_VALIDATION_ERROR",
                            Category = "VALIDATION",
                            Message = $"Validation failed: Missing required property '{fieldName}' for capability '{descriptor.Id}'."
                        };
                        sw.Stop();
                        response.ExecutionDurationMs = sw.Elapsed.TotalMilliseconds;
                        await RecordAuditAsync(request, principal, response, sw.Elapsed.TotalMilliseconds).ConfigureAwait(false);
                        return response;
                    }
                }
            }

            var executor = _registry.GetExecutor(request.CapabilityId);
            if (executor == null)
            {
                response.Status = ExecutionStatus.Failed;
                response.Error = new UccpError
                {
                    Code = "UCCP_500_EXECUTOR_MISSING",
                    Category = "INTERNAL_FAULT",
                    Message = $"No executor bound to capability '{request.CapabilityId}'."
                };
                sw.Stop();
                response.ExecutionDurationMs = sw.Elapsed.TotalMilliseconds;
                await RecordAuditAsync(request, principal, response, sw.Elapsed.TotalMilliseconds).ConfigureAwait(false);
                return response;
            }

            // 4. Hydrate Execution Context
            var execContext = await _contextEngine.HydrateExecutionContextAsync(principal, request.ContextHandle).ConfigureAwait(false);

            // 5. Evaluate Policy (ABAC)
            var policyDecision = await _policyEngine.EvaluateAsync(principal, descriptor, request).ConfigureAwait(false);
            if (!policyDecision.IsAllowed)
            {
                if (policyDecision.RequiresHumanApproval)
                {
                    var ticketId = $"appr_ticket:{Guid.NewGuid():N}";
                    response.Status = ExecutionStatus.PendingApproval;
                    response.Summary = $"[SUSPENDED] {policyDecision.DenialReason} Generated ticket: {ticketId}";
                    response.ContextHandle = ticketId;
                    sw.Stop();
                    response.ExecutionDurationMs = sw.Elapsed.TotalMilliseconds;
                    await RecordAuditAsync(request, principal, response, sw.Elapsed.TotalMilliseconds).ConfigureAwait(false);
                    return response;
                }

                response.Status = ExecutionStatus.Failed;
                response.Error = new UccpError
                {
                    Code = policyDecision.RemediationCode ?? "UCCP_403_FORBIDDEN",
                    Category = "AUTHORIZATION",
                    Message = policyDecision.DenialReason
                };
                sw.Stop();
                response.ExecutionDurationMs = sw.Elapsed.TotalMilliseconds;
                await RecordAuditAsync(request, principal, response, sw.Elapsed.TotalMilliseconds).ConfigureAwait(false);
                return response;
            }

            // 6. Execute Capability with timeout
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                cts.CancelAfter(request.Options?.TimeoutMs ?? descriptor.TimeoutMs);

                try
                {
                    var result = await executor.ExecuteAsync(request.Payload, execContext, cts.Token).ConfigureAwait(false);

                    if (!result.Success)
                    {
                        response.Status = ExecutionStatus.Failed;
                        response.Error = new UccpError
                        {
                            Code = result.ErrorCode ?? "UCCP_500_EXECUTION_FAILED",
                            Category = "EXECUTION",
                            Message = result.ErrorMessage
                        };
                    }
                    else
                    {
                        response.Status = ExecutionStatus.Success;
                        response.Summary = result.Summary;
                        response.Data = result.Data ?? new JObject();

                        // 7. Context Handle & Token Diet Optimization
                        if (result.RawContextPayload != null)
                        {
                            var handle = await _contextEngine.StoreContextAsync(
                                execContext.TenantId,
                                result.RawContextPayload,
                                TimeSpan.FromMinutes(15)).ConfigureAwait(false);

                            response.ContextHandle = handle;

                            if (string.IsNullOrEmpty(response.Summary) && result.RawContextPayload is ICollection collection)
                            {
                                response.Summary = $"Executed successfully. Output contains {collection.Count} records. Context handle: {handle}";
                            }
                        }

                        // 8. Mask Sensitive Fields
                        if (policyDecision.MaskedFields != null && policyDecision.MaskedFields.Count > 0 && response.Data != null)
                        {
                            MaskProperties(response.Data, policyDecision.MaskedFields);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    response.Status = ExecutionStatus.Failed;
                    response.Error = new UccpError
                    {
                        Code = "UCCP_504_DEADLINE_EXCEEDED",
                        Category = "TIMEOUT",
                        Message = $"Capability execution timed out after {request.Options?.TimeoutMs ?? descriptor.TimeoutMs}ms."
                    };
                }
                catch (Exception ex)
                {
                    response.Status = ExecutionStatus.Failed;
                    response.Error = new UccpError
                    {
                        Code = "UCCP_500_INTERNAL_ERROR",
                        Category = "INTERNAL_FAULT",
                        Message = ex.Message
                    };
                }
            }

            sw.Stop();
            response.ExecutionDurationMs = sw.Elapsed.TotalMilliseconds;

            // Cache Idempotency result
            if (!string.IsNullOrEmpty(idemKey))
            {
                _idempotencyStore[idemKey] = new IdempotencyRecord
                {
                    IsCompleted = true,
                    CachedResponse = response,
                    ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5)
                };
            }

            await RecordAuditAsync(request, principal, response, sw.Elapsed.TotalMilliseconds).ConfigureAwait(false);
            return response;
        }

        private async Task RecordAuditAsync(
            UccpRequestEnvelope request,
            ClaimsPrincipal principal,
            UccpResponseEnvelope response,
            double durationMs)
        {
            if (_auditSink == null) return;

            try
            {
                var auditEvent = new AuditEvent
                {
                    RequestId = response.RequestId,
                    CorrelationId = response.CorrelationId,
                    CapabilityId = request?.CapabilityId,
                    ActorId = principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? principal?.Identity?.Name ?? "anonymous",
                    TenantId = principal?.FindFirst("tenant_id")?.Value ?? "default",
                    Status = response.Status,
                    DurationMs = durationMs,
                    Summary = response.Summary,
                    ErrorCode = response.Error?.Code
                };

                await _auditSink.RecordAsync(auditEvent).ConfigureAwait(false);
            }
            catch
            {
                // Never fail the main business path if audit logging encounters a transient error
            }
        }

        private static void MaskProperties(JToken token, System.Collections.Generic.List<string> maskedFields)
        {
            if (token is JObject obj)
            {
                foreach (var prop in obj.Properties())
                {
                    if (maskedFields.Contains(prop.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        prop.Value = "[CONFIDENTIAL_MASKED]";
                    }
                    else
                    {
                        MaskProperties(prop.Value, maskedFields);
                    }
                }
            }
            else if (token is JArray arr)
            {
                foreach (var item in arr)
                {
                    MaskProperties(item, maskedFields);
                }
            }
        }
    }
}
