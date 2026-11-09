using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using UCCP.Core.Contracts.Descriptors;
using UCCP.Core.Contracts.Envelopes;
using UCCP.Core.Contracts.Security;

namespace UCCP.Runtime.Security
{
    public class AbacPolicyEngine : IPolicyEngine
    {
        public Task<PolicyDecision> EvaluateAsync(
            ClaimsPrincipal principal,
            CapabilityDescriptor capability,
            UccpRequestEnvelope request)
        {
            if (capability == null) throw new ArgumentNullException(nameof(capability));

            // 1. Unauthenticated check for non-public capabilities
            if (capability.Security.RequiredRoles.Count > 0 || capability.Security.RequiredPermissions.Count > 0)
            {
                if (principal == null || principal.Identity == null || !principal.Identity.IsAuthenticated)
                {
                    return Task.FromResult(PolicyDecision.Deny(
                        "Authentication required: Request missing valid credentials.",
                        "UCCP_401_UNAUTHENTICATED"));
                }
            }

            // 2. Role-based check
            if (capability.Security.RequiredRoles.Count > 0 && principal != null)
            {
                bool hasRole = capability.Security.RequiredRoles.Any(r => principal.IsInRole(r));
                if (!hasRole)
                {
                    return Task.FromResult(PolicyDecision.Deny(
                        $"Insufficient privileges: Required one of roles [{string.Join(", ", capability.Security.RequiredRoles)}].",
                        "UCCP_403_ROLE_DENIED"));
                }
            }

            // 3. Human Approval Gate
            if (capability.Security.HumanApprovalRequired)
            {
                // Check if the user already provided an approved human token
                var approvalToken = request?.Payload?["approvalToken"]?.ToString();
                if (string.IsNullOrEmpty(approvalToken))
                {
                    return Task.FromResult(PolicyDecision.SuspendForApproval(
                        $"Capability '{capability.Id}' requires explicit human manager confirmation."));
                }
            }

            // 4. Threshold limit check (e.g. approval value)
            if (request?.Payload?["totalAmount"] != null && principal != null)
            {
                var amount = (decimal)request.Payload["totalAmount"];
                var limitClaim = principal.FindFirst("approval_limit")?.Value;
                if (decimal.TryParse(limitClaim, out var userLimit) && amount > userLimit)
                {
                    return Task.FromResult(PolicyDecision.Deny(
                        $"Approval limit exceeded: Operation amount (${amount}) exceeds user authorized threshold (${userLimit}).",
                        "UCCP_403_LIMIT_EXCEEDED"));
                }
            }

            // 5. Field-level masking evaluation (e.g. Cost clearance)
            var maskedFields = new List<string>();
            if (principal != null && !principal.HasClaim("permission", "finance.view_unit_cost"))
            {
                maskedFields.Add("unitCost");
                maskedFields.Add("profitMargin");
                maskedFields.Add("totalCost");
            }

            return Task.FromResult(PolicyDecision.Allow(maskedFields));
        }
    }
}
