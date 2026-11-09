using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UCCP.Adapters.InProc;
using UCCP.Adapters.Mcp;
using UCCP.Core.Contracts.Attributes;
using UCCP.Core.Contracts.Context;
using UCCP.Core.Contracts.Descriptors;
using UCCP.Core.Contracts.Envelopes;
using UCCP.Core.Contracts.Execution;
using UCCP.Core.Contracts.Workflow;
using UCCP.Runtime;
using UCCP.Runtime.Context;
using UCCP.Runtime.Registry;
using UCCP.Runtime.Security;
using UCCP.Runtime.Workflow;
using Xunit;

namespace UCCP.Tests
{
    public class UccpComprehensiveTests
    {
        private readonly InMemoryCapabilityRegistry _registry;
        private readonly InMemoryContextEngine _contextEngine;
        private readonly AbacPolicyEngine _policyEngine;
        private readonly TestAuditSink _auditSink;
        private readonly DefaultUccpRuntime _runtime;
        private readonly InProcInvoker _inProcInvoker;
        private readonly McpServerDispatcher _mcpDispatcher;
        private readonly LightweightDagRunner _dagRunner;

        public UccpComprehensiveTests()
        {
            _registry = new InMemoryCapabilityRegistry();
            _contextEngine = new InMemoryContextEngine("test_super_secret_key_12345");
            _policyEngine = new AbacPolicyEngine();
            _auditSink = new TestAuditSink();
            _runtime = new DefaultUccpRuntime(_registry, _contextEngine, _policyEngine, _auditSink);
            _inProcInvoker = new InProcInvoker(_runtime);
            _mcpDispatcher = new McpServerDispatcher(_runtime, _registry);
            _dagRunner = new LightweightDagRunner(_registry);

            RegisterSampleCapabilities();
        }

        private void RegisterSampleCapabilities()
        {
            // 1. Inventory Stock Query with InputSchema
            _registry.Register(new CapabilityDescriptor
            {
                Id = "inventory.material.stock.query@v1.0",
                Name = "Query Material Stock Balance",
                Domain = "inventory",
                Subdomain = "material",
                Description = "Returns physical and available warehouse stock balances.",
                Action = ActionKind.Read,
                Tags = new List<string> { "stock", "material", "inventory", "balance" },
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""materialCode"": { ""type"": ""string"" }
                    },
                    ""required"": [""materialCode""]
                }"),
                Security = new SecurityMetadata
                {
                    RequiredRoles = new List<string> { "WarehouseStaff", "Planner", "Manager" }
                }
            }, new MockExecutor((payload, ctx) =>
            {
                var materialCode = payload["materialCode"]?.ToString() ?? "RM-001";
                return new ExecutionResult
                {
                    Success = true,
                    Summary = $"Stock balance for {materialCode}: 150 KG in Warehouse 1.",
                    Data = JObject.FromObject(new
                    {
                        materialCode = materialCode,
                        warehouseId = 1,
                        quantity = 150.0,
                        unitCost = 1.25,
                        safetyStock = 500.0
                    })
                };
            }));

            // 2. High-value Purchase Order Approval (Requires Human Manager)
            _registry.Register(new CapabilityDescriptor
            {
                Id = "purchasing.po.approve@v1.0",
                Name = "Approve Purchase Order",
                Domain = "purchasing",
                Subdomain = "po",
                Description = "Approves a purchase order and releases it to vendor.",
                Action = ActionKind.Execute,
                Tags = new List<string> { "po", "purchase", "approval" },
                Security = new SecurityMetadata
                {
                    RequiredRoles = new List<string> { "PurchasingManager", "Director" },
                    HumanApprovalRequired = true
                }
            }, new MockExecutor((payload, ctx) =>
            {
                var poId = payload["poId"]?.ToString() ?? "PO-001";
                return new ExecutionResult
                {
                    Success = true,
                    Summary = $"Purchase Order {poId} approved successfully.",
                    Data = JObject.FromObject(new { poId = poId, status = "Approved" })
                };
            }));

            // 3. Finance Ledger Query (Confidential Admin)
            _registry.Register(new CapabilityDescriptor
            {
                Id = "finance.ledger.query@v1.0",
                Name = "Query General Ledger Entries",
                Domain = "finance",
                Subdomain = "ledger",
                Description = "Queries confidential balance sheets and ledger accounts.",
                Action = ActionKind.Read,
                Tags = new List<string> { "finance", "ledger", "pnl" },
                Security = new SecurityMetadata
                {
                    RequiredRoles = new List<string> { "ChiefAccountant" }
                }
            }, new MockExecutor((payload, ctx) =>
            {
                return new ExecutionResult
                {
                    Success = true,
                    Data = JObject.FromObject(new { revenue = 1000000.0, netProfit = 250000.0 })
                };
            }));
        }

        #region Test Suite 1: Progressive Discovery & Token Optimization

        [Fact]
        public async Task ProgressiveDiscovery_Stage1_ReturnsDomains()
        {
            var user = CreatePrincipal("staff_01", "WarehouseStaff");
            var domains = await _registry.GetDomainsAsync(user);

            Assert.NotNull(domains);
            Assert.Contains(domains, d => d.Domain.Equals("inventory", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task ProgressiveDiscovery_Stage2_FiltersByKeywordAndDomain()
        {
            var user = CreatePrincipal("staff_01", "WarehouseStaff");
            var results = await _registry.SearchCapabilitiesAsync("inventory", "stock balance", user);

            Assert.Single(results);
            Assert.Equal("inventory.material.stock.query@v1.0", results[0].Id);
        }

        [Fact]
        public async Task ProgressiveDiscovery_HidesUnauthorizedCapabilities()
        {
            var staffUser = CreatePrincipal("staff_01", "WarehouseStaff");
            var financeResults = await _registry.SearchCapabilitiesAsync("finance", "ledger", staffUser);

            Assert.Empty(financeResults);

            var accountantUser = CreatePrincipal("cfo_01", "ChiefAccountant");
            var accountantResults = await _registry.SearchCapabilitiesAsync("finance", "ledger", accountantUser);

            Assert.Single(accountantResults);
        }

        #endregion

        #region Test Suite 2: Context Engine, Opaque Handles & HMAC Verification

        [Fact]
        public async Task ContextEngine_StoresLargeTable_AndGeneratesOpaqueHandle()
        {
            var rows = Enumerable.Range(1, 1000)
                .Select(i => new { ItemId = i, Name = $"Material-{i}", Qty = i * 10 })
                .ToList();

            var handle = await _contextEngine.StoreContextAsync("tenant_acme", rows, TimeSpan.FromMinutes(10));

            Assert.NotNull(handle);
            Assert.StartsWith("ctx_hnd:tenant_acme:", handle);

            var resolved = await _contextEngine.ResolveContextAsync<System.Collections.IList>("tenant_acme", handle);
            Assert.NotNull(resolved);
            Assert.Equal(1000, resolved.Count);
        }

        [Fact]
        public async Task ContextEngine_DetectsTamperedHmacSignature_RejectsAccess()
        {
            var handle = await _contextEngine.StoreContextAsync("tenant_acme", "Secret Payload", TimeSpan.FromMinutes(5));

            // Tamper signature
            var parts = handle.Split(':');
            var tamperedHandle = $"ctx_hnd:{parts[1]}:{parts[2]}:INVALID_SIG";

            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            {
                await _contextEngine.ResolveContextAsync<string>("tenant_acme", tamperedHandle);
            });
        }

        [Fact]
        public async Task ContextEngine_EnforcesTenantIsolation_PreventsTenantBleed()
        {
            var handle = await _contextEngine.StoreContextAsync("tenant_A", "Secret Data A", TimeSpan.FromMinutes(5));

            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            {
                await _contextEngine.ResolveContextAsync<string>("tenant_B", handle);
            });
        }

        #endregion

        #region Test Suite 3: Security, ABAC & Input Validation

        [Fact]
        public async Task Runtime_ValidatesInputSchema_FailFastOnMissingRequiredField()
        {
            var user = CreatePrincipal("staff_01", "WarehouseStaff");
            var request = new UccpRequestEnvelope
            {
                CapabilityId = "inventory.material.stock.query@v1.0",
                Payload = new JObject() // Missing required 'materialCode'
            };

            var response = await _runtime.ExecuteAsync(request, user);

            Assert.Equal(ExecutionStatus.Failed, response.Status);
            Assert.Equal("UCCP_400_VALIDATION_ERROR", response.Error.Code);
            Assert.Contains("materialCode", response.Error.Message);
        }

        [Fact]
        public async Task PolicyEngine_DeniesUnauthenticatedCaller()
        {
            var request = new UccpRequestEnvelope
            {
                CapabilityId = "inventory.material.stock.query@v1.0",
                Payload = JObject.FromObject(new { materialCode = "RM-001" })
            };

            var response = await _runtime.ExecuteAsync(request, principal: null);

            Assert.Equal(ExecutionStatus.Failed, response.Status);
            Assert.Equal("UCCP_401_UNAUTHENTICATED", response.Error.Code);
        }

        [Fact]
        public async Task PolicyEngine_DeniesUnauthorizedRole()
        {
            var unauthorizedUser = CreatePrincipal("intern_01", "InternRole");
            var request = new UccpRequestEnvelope
            {
                CapabilityId = "inventory.material.stock.query@v1.0",
                Payload = JObject.FromObject(new { materialCode = "RM-001" })
            };

            var response = await _runtime.ExecuteAsync(request, principal: unauthorizedUser);

            Assert.Equal(ExecutionStatus.Failed, response.Status);
            Assert.Equal("UCCP_403_ROLE_DENIED", response.Error.Code);
        }

        [Fact]
        public async Task PolicyEngine_SuspendsAction_WhenHumanApprovalRequired()
        {
            var manager = CreatePrincipal("mgr_01", "PurchasingManager");
            var request = new UccpRequestEnvelope
            {
                CapabilityId = "purchasing.po.approve@v1.0",
                Payload = JObject.FromObject(new { poId = "PO-2026-99" })
            };

            var response = await _runtime.ExecuteAsync(request, principal: manager);

            Assert.Equal(ExecutionStatus.PendingApproval, response.Status);
            Assert.StartsWith("appr_ticket:", response.ContextHandle);
            Assert.Contains("[SUSPENDED]", response.Summary);

            request.Payload["approvalToken"] = "token_signed_by_human_faceid";
            var successResponse = await _runtime.ExecuteAsync(request, principal: manager);

            Assert.Equal(ExecutionStatus.Success, successResponse.Status);
        }

        [Fact]
        public async Task PolicyEngine_MasksConfidentialCost_ForNonFinanceUsers()
        {
            var warehouseStaff = CreatePrincipal("staff_01", "WarehouseStaff");
            var request = new UccpRequestEnvelope
            {
                CapabilityId = "inventory.material.stock.query@v1.0",
                Payload = JObject.FromObject(new { materialCode = "RM-001" })
            };

            var response = await _runtime.ExecuteAsync(request, principal: warehouseStaff);

            Assert.Equal(ExecutionStatus.Success, response.Status);
            Assert.NotNull(response.Data);
            Assert.Equal(150.0, (double?)response.Data["quantity"]);
            Assert.Equal("[CONFIDENTIAL_MASKED]", (string?)response.Data["unitCost"]);
        }

        #endregion

        #region Test Suite 4: Idempotency Key Engine

        [Fact]
        public async Task Runtime_EnforcesIdempotency_ReturnsCachedResultWithoutReexecution()
        {
            var user = CreatePrincipal("staff_01", "WarehouseStaff");
            int executionCount = 0;

            _registry.Register(new CapabilityDescriptor
            {
                Id = "order.payment.process@v1.0",
                Action = ActionKind.Execute
            }, new MockExecutor((p, c) =>
            {
                executionCount++;
                return new ExecutionResult
                {
                    Success = true,
                    Summary = $"Processed payment attempt #{executionCount}"
                };
            }));

            var request = new UccpRequestEnvelope
            {
                CapabilityId = "order.payment.process@v1.0",
                IdempotencyKey = "order_payment_unique_uuid_9999",
                Payload = JObject.FromObject(new { orderId = 100 })
            };

            // Call 1
            var res1 = await _runtime.ExecuteAsync(request, user);
            Assert.Equal(ExecutionStatus.Success, res1.Status);
            Assert.Equal("Processed payment attempt #1", res1.Summary);
            Assert.Equal(1, executionCount);

            // Call 2 with identical IdempotencyKey
            var res2 = await _runtime.ExecuteAsync(request, user);
            Assert.Equal(ExecutionStatus.Success, res2.Status);
            Assert.Equal("Processed payment attempt #1", res2.Summary);
            Assert.Equal(1, executionCount); // MUST NOT re-execute!
            Assert.Equal(0.0, res2.ExecutionDurationMs); // Instant cache hit
        }

        #endregion

        #region Test Suite 5: Workflow DAG & Pre-Validation

        [Fact]
        public async Task WorkflowEngine_PreValidatesTopology_DetectsCycleBeforeExecution()
        {
            // Workflow with circular dependency: s1 -> s2 -> s1
            var cyclicWorkflow = new WorkflowDefinition
            {
                WorkflowId = "wf.cyclic.bad@v1.0",
                Steps = new List<WorkflowStep>
                {
                    new WorkflowStep { StepId = "s1", CapabilityId = "test.s1", DependsOn = new List<string> { "s2" } },
                    new WorkflowStep { StepId = "s2", CapabilityId = "test.s2", DependsOn = new List<string> { "s1" } }
                }
            };

            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                _dagRunner.ValidateWorkflow(cyclicWorkflow);
            });

            Assert.Contains("Circular dependency", ex.Message);
        }

        [Fact]
        public async Task WorkflowEngine_ExecutesCompensation_WhenDownstreamStepFails()
        {
            var compensated = false;

            _registry.Register(new CapabilityDescriptor { Id = "saga.deduct@v1.0" }, new MockExecutor((p, c) =>
                new ExecutionResult { Success = true, Data = JObject.FromObject(new { deducted = 50 }) }));

            _registry.Register(new CapabilityDescriptor { Id = "saga.revert_deduct@v1.0" }, new MockExecutor((p, c) =>
            {
                compensated = true;
                return new ExecutionResult { Success = true };
            }));

            _registry.Register(new CapabilityDescriptor { Id = "saga.failing_payment@v1.0" }, new MockExecutor((p, c) =>
                new ExecutionResult { Success = false, ErrorMessage = "Payment gateway down" }));

            var workflow = new WorkflowDefinition
            {
                WorkflowId = "wf.saga.test@v1.0",
                Steps = new List<WorkflowStep>
                {
                    new WorkflowStep
                    {
                        StepId = "step_deduct",
                        CapabilityId = "saga.deduct@v1.0",
                        CompensatingCapabilityId = "saga.revert_deduct@v1.0"
                    },
                    new WorkflowStep
                    {
                        StepId = "step_pay",
                        CapabilityId = "saga.failing_payment@v1.0",
                        DependsOn = new List<string> { "step_deduct" }
                    }
                }
            };

            var ctx = new ExecutionContextData();
            var result = await _dagRunner.ExecuteWorkflowAsync(workflow, new JObject(), ctx, CancellationToken.None);

            Assert.False(result.Success);
            Assert.True(compensated, "Compensating transaction MUST execute when downstream step fails.");
        }

        #endregion

        #region Test Suite 6: Declarative Attributes & Assembly Auto-Discovery

        [Fact]
        public async Task Registry_RegisterFromAssembly_DiscoversDecoratedExecutors()
        {
            var localRegistry = new InMemoryCapabilityRegistry();
            localRegistry.RegisterFromAssembly(typeof(UccpComprehensiveTests).Assembly);

            var descriptor = await localRegistry.GetDescriptorAsync("test.sample.decorated@v1.0");
            Assert.NotNull(descriptor);
            Assert.Equal("Sample Auto-Discovered Capability", descriptor.Name);
            Assert.Equal("demo", descriptor.Domain);
            Assert.Contains("ManagerRole", descriptor.Security.RequiredRoles);
        }

        #endregion

        #region Test Suite 7: MCP Adapter & Stage 3 Schema Hydration

        [Fact]
        public async Task McpAdapter_Stage3_ReturnsDetailedCapabilitySchema()
        {
            var mcpReq = new McpRequest
            {
                Method = "tools/call",
                Id = 10,
                Params = JObject.FromObject(new
                {
                    name = "uccp_get_capability_schema",
                    arguments = new { capabilityId = "inventory.material.stock.query@v1.0" }
                })
            };

            var mcpRes = await _mcpDispatcher.DispatchAsync(mcpReq);

            Assert.Null(mcpRes.Error);
            var callResult = mcpRes.Result as CallToolResult;
            Assert.NotNull(callResult);
            Assert.False(callResult.IsError);
            Assert.Contains("materialCode", callResult.Content[0].Text);
            Assert.Contains("inventory.material.stock.query@v1.0", callResult.Content[0].Text);
        }

        #endregion

        #region Test Suite 8: Pluggable Audit Sink

        [Fact]
        public async Task Runtime_RecordsAuditEvent_OnExecution()
        {
            var user = CreatePrincipal("staff_audit", "WarehouseStaff");
            var request = new UccpRequestEnvelope
            {
                CapabilityId = "inventory.material.stock.query@v1.0",
                Payload = JObject.FromObject(new { materialCode = "RM-001" })
            };

            await _runtime.ExecuteAsync(request, user);

            Assert.NotEmpty(_auditSink.RecordedEvents);
            var ev = _auditSink.RecordedEvents.Last();
            Assert.Equal("inventory.material.stock.query@v1.0", ev.CapabilityId);
            Assert.Equal("staff_audit", ev.ActorId);
            Assert.Equal(ExecutionStatus.Success, ev.Status);
        }

        #endregion

        private static ClaimsPrincipal CreatePrincipal(string name, string role)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, name),
                new Claim(ClaimTypes.Name, name),
                new Claim(ClaimTypes.Role, role),
                new Claim("tenant_id", "tenant_acme"),
                new Claim("approval_limit", "50000.0")
            };

            var identity = new ClaimsIdentity(claims, "TestAuth");
            return new ClaimsPrincipal(identity);
        }

        private class MockExecutor : ICapabilityExecutor
        {
            private readonly Func<JObject, ExecutionContextData, ExecutionResult> _func;

            public MockExecutor(Func<JObject, ExecutionContextData, ExecutionResult> func)
            {
                _func = func;
            }

            public Task<ExecutionResult> ExecuteAsync(
                JObject payload,
                ExecutionContextData context,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(_func(payload, context));
            }
        }

        private class TestAuditSink : IAuditSink
        {
            public List<AuditEvent> RecordedEvents { get; } = new List<AuditEvent>();

            public Task RecordAsync(AuditEvent auditEvent)
            {
                RecordedEvents.Add(auditEvent);
                return Task.FromResult(0);
            }
        }
    }

    [UccpCapability("test.sample.decorated@v1.0", Domain = "demo", Name = "Sample Auto-Discovered Capability", Description = "Test capability")]
    [UccpSecurity("ManagerRole")]
    public class SampleDecoratedExecutor : ICapabilityExecutor
    {
        public Task<ExecutionResult> ExecuteAsync(JObject payload, ExecutionContextData context, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ExecutionResult { Success = true, Summary = "Decorated executed." });
        }
    }
}
