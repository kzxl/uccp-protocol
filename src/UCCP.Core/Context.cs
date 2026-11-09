using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;

namespace UCCP.Core.Contracts.Context
{
    public class ExecutionContextData
    {
        public string TenantId { get; set; } = "default";
        public string UserId { get; set; }
        public string UserName { get; set; }
        public List<string> Roles { get; set; } = new List<string>();
        public List<string> Permissions { get; set; } = new List<string>();
        public Dictionary<string, object> BusinessAttributes { get; set; } = new Dictionary<string, object>();
        public Dictionary<string, object> AmbientData { get; set; } = new Dictionary<string, object>();
        public ClaimsPrincipal Principal { get; set; }
    }

    public interface IContextEngine
    {
        Task<string> StoreContextAsync(string tenantId, object data, TimeSpan ttl);

        Task<T> ResolveContextAsync<T>(string tenantId, string contextHandle);

        Task<ExecutionContextData> HydrateExecutionContextAsync(ClaimsPrincipal principal, string contextHandle);
    }
}
