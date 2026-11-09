using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using UCCP.Core.Contracts.Descriptors;
using UCCP.Core.Contracts.Envelopes;

namespace UCCP.Core.Contracts.Security
{
    public class PolicyDecision
    {
        public bool IsAllowed { get; set; }
        public string DenialReason { get; set; }
        public string RemediationCode { get; set; }
        public bool RequiresHumanApproval { get; set; }
        public List<string> MaskedFields { get; set; } = new List<string>();

        public static PolicyDecision Allow(List<string> maskedFields = null)
        {
            return new PolicyDecision
            {
                IsAllowed = true,
                MaskedFields = maskedFields ?? new List<string>()
            };
        }

        public static PolicyDecision Deny(string reason, string remediationCode = null)
        {
            return new PolicyDecision
            {
                IsAllowed = false,
                DenialReason = reason,
                RemediationCode = remediationCode
            };
        }

        public static PolicyDecision SuspendForApproval(string reason)
        {
            return new PolicyDecision
            {
                IsAllowed = false,
                RequiresHumanApproval = true,
                DenialReason = reason
            };
        }
    }

    public interface IPolicyEngine
    {
        Task<PolicyDecision> EvaluateAsync(
            ClaimsPrincipal principal,
            CapabilityDescriptor capability,
            UccpRequestEnvelope request);
    }
}
