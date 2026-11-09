using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UCCP.Core.Contracts.Context;
using UCCP.Core.Contracts.Descriptors;
using UCCP.Core.Contracts.Envelopes;

namespace UCCP.Core.Contracts.Execution
{
    public class ExecutionResult
    {
        public bool Success { get; set; } = true;
        public string Summary { get; set; }
        public JObject Data { get; set; }
        public object RawContextPayload { get; set; }
        public string ErrorMessage { get; set; }
        public string ErrorCode { get; set; }
    }

    public interface ICapabilityExecutor
    {
        Task<ExecutionResult> ExecuteAsync(
            JObject payload,
            ExecutionContextData context,
            CancellationToken cancellationToken);
    }

    public interface IUccpRuntime
    {
        Task<UccpResponseEnvelope> ExecuteAsync(
            UccpRequestEnvelope request,
            ClaimsPrincipal principal = null,
            CancellationToken cancellationToken = default(CancellationToken));
    }

    public interface ICapabilityRegistry
    {
        void Register(CapabilityDescriptor descriptor, ICapabilityExecutor executor);

        Task<CapabilityDescriptor> GetDescriptorAsync(string capabilityId);

        Task<IReadOnlyList<DomainSummary>> GetDomainsAsync(ClaimsPrincipal principal = null);

        Task<IReadOnlyList<CapabilitySummary>> SearchCapabilitiesAsync(
            string domain,
            string query,
            ClaimsPrincipal principal = null,
            int maxResults = 10);
    }

    public interface IUccpInvoker
    {
        Task<UccpResponseEnvelope> InvokeAsync(
            string capabilityId,
            object payload,
            ClaimsPrincipal principal = null,
            CancellationToken cancellationToken = default(CancellationToken));
    }
}
