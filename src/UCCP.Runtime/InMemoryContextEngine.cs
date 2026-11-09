using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using UCCP.Core.Contracts.Context;

namespace UCCP.Runtime.Context
{
    public class InMemoryContextEngine : IContextEngine
    {
        private class CacheItem
        {
            public string TenantId { get; set; }
            public object Data { get; set; }
            public DateTime ExpiresAtUtc { get; set; }
        }

        private readonly ConcurrentDictionary<string, CacheItem> _store =
            new ConcurrentDictionary<string, CacheItem>(StringComparer.Ordinal);

        public Task<string> StoreContextAsync(string tenantId, object data, TimeSpan ttl)
        {
            if (string.IsNullOrEmpty(tenantId)) tenantId = "default";

            var handle = $"ctx_hnd:{tenantId}:{Guid.NewGuid():N}";
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

            if (_store.TryGetValue(contextHandle, out var item))
            {
                if (DateTime.UtcNow > item.ExpiresAtUtc)
                {
                    _store.TryRemove(contextHandle, out _);
                    return Task.FromResult(default(T));
                }

                if (!string.IsNullOrEmpty(tenantId) && !item.TenantId.Equals(tenantId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException($"Tenant isolation violation: Context '{contextHandle}' belongs to tenant '{item.TenantId}', not '{tenantId}'.");
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
    }
}
