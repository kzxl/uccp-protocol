using System;
using System.Collections.Generic;
using UCCP.Core.Contracts.Descriptors;

namespace UCCP.Core.Contracts.Attributes
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public class UccpCapabilityAttribute : Attribute
    {
        public string Id { get; }
        public string Name { get; set; }
        public string Domain { get; set; }
        public string Subdomain { get; set; }
        public string Description { get; set; }
        public ActionKind Action { get; set; } = ActionKind.Execute;
        public bool Idempotent { get; set; } = true;
        public int TimeoutMs { get; set; } = 10000;
        public string[] Tags { get; set; } = new string[0];

        public UccpCapabilityAttribute(string id)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
        }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public class UccpSecurityAttribute : Attribute
    {
        public string[] RequiredRoles { get; set; } = new string[0];
        public string[] RequiredPermissions { get; set; } = new string[0];
        public bool HumanApprovalRequired { get; set; } = false;
        public bool RequiresAuditLog { get; set; } = true;

        public UccpSecurityAttribute()
        {
        }

        public UccpSecurityAttribute(params string[] requiredRoles)
        {
            RequiredRoles = requiredRoles ?? new string[0];
        }
    }
}
