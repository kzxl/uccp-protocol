using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UCCP.Core.Contracts.Envelopes;
using UCCP.Core.Contracts.Execution;

namespace UCCP.Adapters.InProc
{
    public class InProcInvoker : IUccpInvoker
    {
        private readonly IUccpRuntime _runtime;

        public InProcInvoker(IUccpRuntime runtime)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        }

        public Task<UccpResponseEnvelope> InvokeAsync(
            string capabilityId,
            object payload,
            ClaimsPrincipal principal = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrEmpty(capabilityId)) throw new ArgumentNullException(nameof(capabilityId));

            JObject jPayload;
            if (payload is JObject jo)
            {
                jPayload = jo;
            }
            else if (payload != null)
            {
                jPayload = JObject.FromObject(payload);
            }
            else
            {
                jPayload = new JObject();
            }

            var request = new UccpRequestEnvelope
            {
                CapabilityId = capabilityId,
                Payload = jPayload
            };

            return _runtime.ExecuteAsync(request, principal, cancellationToken);
        }
    }
}
