using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using UCCP.Core.Contracts.Attributes;
using UCCP.Core.Contracts.Descriptors;
using UCCP.Core.Contracts.Execution;

namespace UCCP.Runtime.Registry
{
    public class InMemoryCapabilityRegistry : ICapabilityRegistry
    {
        private readonly ConcurrentDictionary<string, CapabilityDescriptor> _descriptors =
            new ConcurrentDictionary<string, CapabilityDescriptor>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, ICapabilityExecutor> _executors =
            new ConcurrentDictionary<string, ICapabilityExecutor>(StringComparer.OrdinalIgnoreCase);

        public void Register(CapabilityDescriptor descriptor, ICapabilityExecutor executor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (executor == null) throw new ArgumentNullException(nameof(executor));

            _descriptors[descriptor.Id] = descriptor;
            _executors[descriptor.Id] = executor;
        }

        public void RegisterFromAssembly(Assembly assembly, Func<Type, object> resolver = null)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));

            var executorTypes = assembly.GetTypes()
                .Where(t => !t.IsAbstract && !t.IsInterface && typeof(ICapabilityExecutor).IsAssignableFrom(t));

            foreach (var type in executorTypes)
            {
                var capAttr = type.GetCustomAttribute<UccpCapabilityAttribute>();
                if (capAttr == null) continue;

                var secAttr = type.GetCustomAttribute<UccpSecurityAttribute>();

                var descriptor = new CapabilityDescriptor
                {
                    Id = capAttr.Id,
                    Name = capAttr.Name ?? capAttr.Id,
                    Domain = capAttr.Domain ?? "common",
                    Subdomain = capAttr.Subdomain,
                    Description = capAttr.Description,
                    Action = capAttr.Action,
                    Idempotent = capAttr.Idempotent,
                    TimeoutMs = capAttr.TimeoutMs,
                    Tags = capAttr.Tags != null ? new List<string>(capAttr.Tags) : new List<string>()
                };

                if (secAttr != null)
                {
                    descriptor.Security.RequiredRoles = secAttr.RequiredRoles != null
                        ? new List<string>(secAttr.RequiredRoles)
                        : new List<string>();

                    descriptor.Security.RequiredPermissions = secAttr.RequiredPermissions != null
                        ? new List<string>(secAttr.RequiredPermissions)
                        : new List<string>();

                    descriptor.Security.HumanApprovalRequired = secAttr.HumanApprovalRequired;
                    descriptor.Security.RequiresAuditLog = secAttr.RequiresAuditLog;
                }

                ICapabilityExecutor executor;
                if (resolver != null)
                {
                    executor = (ICapabilityExecutor)resolver(type);
                }
                else
                {
                    executor = (ICapabilityExecutor)Activator.CreateInstance(type);
                }

                Register(descriptor, executor);
            }
        }

        public Task<CapabilityDescriptor> GetDescriptorAsync(string capabilityId)
        {
            if (string.IsNullOrEmpty(capabilityId)) return Task.FromResult<CapabilityDescriptor>(null);

            _descriptors.TryGetValue(capabilityId, out var descriptor);
            return Task.FromResult(descriptor);
        }

        public ICapabilityExecutor GetExecutor(string capabilityId)
        {
            _executors.TryGetValue(capabilityId, out var executor);
            return executor;
        }

        public Task<IReadOnlyList<DomainSummary>> GetDomainsAsync(ClaimsPrincipal principal = null)
        {
            var accessible = FilterByPrincipal(_descriptors.Values, principal);

            var domains = accessible
                .GroupBy(d => d.Domain, StringComparer.OrdinalIgnoreCase)
                .Select(g => new DomainSummary
                {
                    Domain = g.Key,
                    CapabilityCount = g.Count(),
                    Summary = $"Domain {g.Key} providing {g.Count()} operations."
                })
                .OrderBy(d => d.Domain)
                .ToList();

            return Task.FromResult<IReadOnlyList<DomainSummary>>(domains);
        }

        public Task<IReadOnlyList<CapabilitySummary>> SearchCapabilitiesAsync(
            string domain,
            string query,
            ClaimsPrincipal principal = null,
            int maxResults = 10)
        {
            var candidates = _descriptors.Values.AsEnumerable();

            if (!string.IsNullOrEmpty(domain) && !domain.Equals("*", StringComparison.OrdinalIgnoreCase))
            {
                candidates = candidates.Where(c => c.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase));
            }

            candidates = FilterByPrincipal(candidates, principal);

            if (!string.IsNullOrWhiteSpace(query))
            {
                var queryTokens = query.Split(new[] { ' ', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);

                candidates = candidates
                    .Select(c => new
                    {
                        Capability = c,
                        Score = CalculateRelevanceScore(c, queryTokens)
                    })
                    .Where(x => x.Score > 0)
                    .OrderByDescending(x => x.Score)
                    .Select(x => x.Capability);
            }

            var summaries = candidates
                .Take(maxResults)
                .Select(c => new CapabilitySummary
                {
                    Id = c.Id,
                    Name = c.Name,
                    Domain = c.Domain,
                    Description = c.Description,
                    Action = c.Action
                })
                .ToList();

            return Task.FromResult<IReadOnlyList<CapabilitySummary>>(summaries);
        }

        private static int CalculateRelevanceScore(CapabilityDescriptor c, string[] queryTokens)
        {
            int score = 0;
            var text = $"{c.Id} {c.Name} {c.Description} {string.Join(" ", c.Tags)}".ToLowerInvariant();

            foreach (var token in queryTokens)
            {
                var t = token.ToLowerInvariant();
                if (c.Id.ToLowerInvariant().Contains(t)) score += 10;
                if (c.Name != null && c.Name.ToLowerInvariant().Contains(t)) score += 5;
                if (c.Tags != null && c.Tags.Any(tag => tag.Equals(t, StringComparison.OrdinalIgnoreCase))) score += 4;
                if (text.Contains(t)) score += 1;
            }

            return score;
        }

        private static IEnumerable<CapabilityDescriptor> FilterByPrincipal(
            IEnumerable<CapabilityDescriptor> list,
            ClaimsPrincipal principal)
        {
            if (principal == null) return list;

            return list.Where(c =>
            {
                if (c.Security.RequiredRoles == null || c.Security.RequiredRoles.Count == 0)
                    return true;

                return c.Security.RequiredRoles.Any(role => principal.IsInRole(role));
            });
        }
    }
}
