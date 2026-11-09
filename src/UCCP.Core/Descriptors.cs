using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UCCP.Core.Contracts.Descriptors
{
    public enum ActionKind
    {
        Read,
        Write,
        Execute,
        Admin
    }

    public class SecurityMetadata
    {
        public List<string> RequiredPermissions { get; set; } = new List<string>();
        public List<string> RequiredRoles { get; set; } = new List<string>();
        public bool RequiresAuditLog { get; set; } = true;
        public bool HumanApprovalRequired { get; set; } = false;
        public decimal MaxAutoApproveThreshold { get; set; } = 0m;
    }

    public class CapabilityDescriptor
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Domain { get; set; }
        public string Subdomain { get; set; }
        public string Description { get; set; }
        public ActionKind Action { get; set; } = ActionKind.Execute;
        public bool Idempotent { get; set; } = true;
        public bool Transactional { get; set; } = false;
        public int TimeoutMs { get; set; } = 10000;
        public List<string> Tags { get; set; } = new List<string>();
        public SecurityMetadata Security { get; set; } = new SecurityMetadata();
        public JObject InputSchema { get; set; }
        public JObject OutputSchema { get; set; }
    }

    public class DomainSummary
    {
        public string Domain { get; set; }
        public string Summary { get; set; }
        public int CapabilityCount { get; set; }
    }

    public class CapabilitySummary
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Domain { get; set; }
        public string Description { get; set; }
        public ActionKind Action { get; set; }
    }
}
