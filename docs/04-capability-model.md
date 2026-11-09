# UCCP Capability Model & Taxonomy

---

## 1. Core Terminology & Formal Taxonomy

In UCCP, the unit of discovery and execution is the **Capability**, which supersedes the primitive "Tool" paradigm. The model establishes a strict hierarchical taxonomy:

```
Domain (e.g., Inventory)
  └── SubDomain (e.g., Material)
        └── Resource / Entity (e.g., StockIn)
              └── Operation / Verb (e.g., Create, Approve, Reconcile)
```

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                           TAXONOMY DEFINITIONS                              │
├──────────────┬──────────────────────────────────────────────────────────────┤
│ Term         │ Formal Architectural Definition                              │
├──────────────┼──────────────────────────────────────────────────────────────┤
│ **Domain**   │ A bounded business context (e.g., `inventory`, `purchase`,   │
│              │ `finance`, `production`).                                    │
├──────────────┼──────────────────────────────────────────────────────────────┤
│ **Capability**│ A cohesive, versioned business contract encapsulating one or │
│              │ more operations that deliver a distinct enterprise outcome.  │
├──────────────┼──────────────────────────────────────────────────────────────┤
│ **Operation**│ A discrete business verb within a capability (e.g., `query`, │
│              │ `calculate`, `submit`, `approve`, `cancel`).                 │
├──────────────┼──────────────────────────────────────────────────────────────┤
│ **Action**   │ The fundamental data effect: `READ`, `WRITE`, `EXECUTE`, or  │
│              │ `ADMIN`. Used by the Policy Engine for coarse security.      │
├──────────────┼──────────────────────────────────────────────────────────────┤
│ **Resource** │ The target business entity instance (e.g., `warehouse:10`,   │
│              │ `stock_in_ticket:2026-0045`).                                │
├──────────────┼──────────────────────────────────────────────────────────────┤
│ **Contract** │ The immutable declaration of inputs, outputs, preconditions, │
│              │ idempotency guarantees, and error states.                    │
├──────────────┼──────────────────────────────────────────────────────────────┤
│ **Executor** │ The concrete execution binding (C# class, HTTP endpoint,     │
│              │ gRPC service, or Workflow DAG).                              │
└──────────────┴──────────────────────────────────────────────────────────────┘
```

---

## 2. Capability Identification & Naming Convention

Every Capability in UCCP possesses a globally unique, deterministic URI formatted according to the following syntax:

$$\text{CapabilityID} = \text{domain}.\text{subdomain}.\text{entity}.\text{operation}@v\text{Major}.\text{Minor}$$

### Formal Syntax Rules
1. **Case**: Lowercase kebab-case or snake_case with dot separation.
2. **Domain Prefix**: Must match a registered business domain in the Enterprise Catalog.
3. **Version Suffix**: Strict Semantic Versioning (`@v1`, `@v2.1`). Major version increments indicate breaking schema or behavior changes.
4. **Idempotency Tag**: Declares whether the operation is safe to retry automatically upon transient network failure (`idempotent: true/false`).

### Examples
- `inventory.material.stock_in.query@v1.0`: Read-only lookup of material receipts.
- `inventory.material.stock_in.approve@v1.2`: High-risk transactional state modification requiring approval policies.
- `purchasing.supplier.quote.evaluate@v2.0`: Composite calculation comparing supplier quotes against historical purchase trends.

---

## 3. Capability vs. Tool: Granularity Analysis

```
+-----------------------------------+-----------------------------------+
| Atomic Tool Paradigm (MCP)        | UCCP Capability Paradigm          |
+-----------------------------------+-----------------------------------+
| `sql_execute_query`               | `inventory.stock.check_reorder`   |
| `read_excel_file`                 |                                   |
| `send_smtp_email`                 |                                   |
+-----------------------------------+-----------------------------------+
| Result: LLM must write raw SQL,   | Result: LLM calls one high-level  |
| compute mathematical formulas,    | capability; business logic and    |
| and coordinate multi-step flow.   | transactions run in backend code. |
+-----------------------------------+-----------------------------------+
```

### Why Capability-Centricity Wins in Enterprise:
1. **Elimination of SQL Injection & Schema Hallucination**: Exposing raw database tools allows LLMs to formulate malformed or dangerous SQL queries. Capabilities encapsulate verified EF6 / LINQ / Dapper queries behind strict parameters.
2. **Multi-Service Aggregation**: A single capability (e.g., `evaluate_reorder`) can query ERP inventory tables, call an external supplier price API, and compute economic order quantity (EOQ) within the backend runtime, returning only the synthesized recommendation to the LLM.
3. **Independent Non-LLM Invocation**: Because capabilities are standard business contracts, they can be invoked directly by cron jobs, integration queues, or legacy WinForms controllers without involving an LLM.

---

## 4. Capability Descriptor Specification (JSON Schema)

Every capability registered in UCCP provides a machine-readable descriptor:

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "id": "inventory.material.stock_in.approve@v1.0",
  "name": "Approve Material Stock-In Ticket",
  "domain": "inventory",
  "subdomain": "material",
  "description": "Validates warehouse capacity, checks inspection compliance, updates inventory balances, and marks ticket approved.",
  "action": "EXECUTE",
  "idempotent": true,
  "transactional": true,
  "executionMode": "SYNC_OR_ASYNC",
  "timeoutMs": 5000,
  "tags": ["stock", "approval", "material", "finance-impact"],
  "security": {
    "requiredPermissions": ["MDS.Inventory.MaterialStockIn.Approve"],
    "requiredRoles": ["WarehouseManager", "InventorySupervisor"],
    "requiresAuditLog": true,
    "humanApprovalRequired": true
  },
  "inputSchema": {
    "type": "object",
    "properties": {
      "ticketId": {
        "type": "integer",
        "description": "Unique identifier of the Material Stock-In ticket."
      },
      "approvalNotes": {
        "type": "string",
        "maxLength": 500,
        "description": "Optional manager notes recorded in the audit trail."
      }
    },
    "required": ["ticketId"]
  },
  "outputSchema": {
    "type": "object",
    "properties": {
      "ticketId": { "type": "integer" },
      "status": { "type": "string", "enum": ["Approved", "PendingReview"] },
      "approvedAt": { "type": "string", "format": "date-time" },
      "approvedBy": { "type": "string" },
      "inventoryBalanceDelta": { "type": "number" },
      "contextHandle": { "type": "string", "description": "Handle to generated stock audit log." }
    },
    "required": ["ticketId", "status", "approvedAt"]
  }
}
```
