using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UCCP.Core.Contracts.Context;

namespace UCCP.Runtime.Context
{
    public class InMemoryContextEngine : IContextEngine, IDisposable
    {
        private class CacheItem
        {
            public string TenantId { get; set; }
            public object Data { get; set; }
            public DateTime ExpiresAtUtc { get; set; }
        }

        private readonly ConcurrentDictionary<string, CacheItem> _store =
            new ConcurrentDictionary<string, CacheItem>(StringComparer.Ordinal);

        private readonly byte[] _hmacSecret;
        private readonly Timer _cleanupTimer;
        private bool _disposed;

        public InMemoryContextEngine(string secret = null)
        {
            if (string.IsNullOrEmpty(secret))
            {
                // Generate secure random secret for this runtime instance
                _hmacSecret = new byte[32];
                using (var rng = RandomNumberGenerator.Create())
                {
                    rng.GetBytes(_hmacSecret);
                }
            }
            else
            {
                _hmacSecret = Encoding.UTF8.GetBytes(secret);
            }

            // Cleanup expired handles every 60 seconds to prevent RAM leaks
            _cleanupTimer = new Timer(CleanupExpiredHandles, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }

        public Task<string> StoreContextAsync(string tenantId, object data, TimeSpan ttl)
        {
            if (string.IsNullOrEmpty(tenantId)) tenantId = "default";

            var id = Guid.NewGuid().ToString("N");
            var signature = ComputeSignature($"{tenantId}:{id}");
            var handle = $"ctx_hnd:{tenantId}:{id}:{signature}";

            var item = new CacheItem
            {
                TenantId = tenantId,
                Data = data,
                ExpiresAtUtc = DateTime.UtcNow.Add(ttl)
            };

            _store[handle] = item;
            return Task.FromResult(handle);
        }

        public Task<T> ResolveContextAsync<T>(string tenantId, string contextHandle)
        {
            if (string.IsNullOrEmpty(contextHandle)) return Task.FromResult(default(T));

            // Verify Handle Format and HMAC Signature
            var parts = contextHandle.Split(':');
            if (parts.Length != 4 || parts[0] != "ctx_hnd")
            {
                throw new ArgumentException("Invalid context handle format.", nameof(contextHandle));
            }

            var handleTenant = parts[1];
            var handleId = parts[2];
            var providedSignature = parts[3];

            var expectedSignature = ComputeSignature($"{handleTenant}:{handleId}");
            if (!string.Equals(providedSignature, expectedSignature, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException($"Security violation: Context handle '{contextHandle}' signature verification failed (tampered handle).");
            }

            // Enforce Tenant Isolation
            if (!string.IsNullOrEmpty(tenantId) && !handleTenant.Equals(tenantId, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException($"Tenant isolation violation: Context belongs to tenant '{handleTenant}', not '{tenantId}'.");
            }

            if (_store.TryGetValue(contextHandle, out var item))
            {
                if (DateTime.UtcNow > item.ExpiresAtUtc)
                {
                    _store.TryRemove(contextHandle, out _);
                    return Task.FromResult(default(T));
                }

                if (item.Data is T typedData)
                {
                    return Task.FromResult(typedData);
                }
            }

            return Task.FromResult(default(T));
        }

        public Task<ExecutionContextData> HydrateExecutionContextAsync(ClaimsPrincipal principal, string contextHandle)
        {
            var ctx = new ExecutionContextData();

            if (principal != null)
            {
                ctx.Principal = principal;
                ctx.UserId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? principal.Identity?.Name ?? "anonymous";
                ctx.UserName = principal.Identity?.Name ?? ctx.UserId;
                ctx.Roles = principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
                ctx.TenantId = principal.FindFirst("tenant_id")?.Value ?? "default";

                var dept = principal.FindFirst("department")?.Value;
                if (!string.IsNullOrEmpty(dept))
                {
                    ctx.BusinessAttributes["Department"] = dept;
                }

                var limitStr = principal.FindFirst("approval_limit")?.Value;
                if (decimal.TryParse(limitStr, out var limit))
                {
                    ctx.BusinessAttributes["ApprovalLimit"] = limit;
                }
            }

            return Task.FromResult(ctx);
        }

        private string ComputeSignature(string rawData)
        {
            using (var hmac = new HMACSHA256(_hmacSecret))
            {
                var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawData));
                return Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            }
        }

        private void CleanupExpiredHandles(object state)
        {
            var now = DateTime.UtcNow;
            foreach (var kvp in _store)
            {
                if (now > kvp.Value.ExpiresAtUtc)
                {
                    _store.TryRemove(kvp.Key, out _);
                }
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _cleanupTimer?.Dispose();
                _disposed = true;
            }
        }
    }
}
