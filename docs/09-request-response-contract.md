# UCCP Request & Response Contract Specification

---

## 1. The 3-Tier Envelope Architecture

To decouple the physical wire transmission from execution governance and domain logic, UCCP defines a three-tier envelope hierarchy:

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                      1. TRANSPORT ENVELOPE                                  │
│   (HTTP Headers / gRPC Metadata / WebSocket Frame / Stdio Line Delimiter)   │
│   Authorization: Bearer <jwt>  |  traceparent: 00-4bf92...                 │
├─────────────────────────────────────────────────────────────────────────────┤
│                      2. UCCP EXECUTION ENVELOPE                             │
│   { "protocolVersion": "1.0", "requestId": "req_123", "capabilityId": ... } │
├─────────────────────────────────────────────────────────────────────────────┤
│                      3. BUSINESS PAYLOAD                                    │
│   { "ticketId": 10042, "approvalNotes": "Batch verified" }                  │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## 2. Request Envelope Specification

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "title": "UccpRequestEnvelope",
  "type": "object",
  "properties": {
    "protocolVersion": {
      "type": "string",
      "enum": ["1.0"],
      "description": "UCCP protocol version."
    },
    "requestId": {
      "type": "string",
      "format": "uuid",
      "description": "Unique identifier for this single execution attempt."
    },
    "correlationId": {
      "type": "string",
      "description": "End-to-end conversation or workflow correlation identifier."
    },
    "capabilityId": {
      "type": "string",
      "pattern": "^[a-z0-9_.-]+@[a-z0-9_.-]+$",
      "description": "Target capability identifier with version (e.g., inventory.stock.check@v1.0)."
    },
    "contextHandle": {
      "type": "string",
      "description": "Optional opaque pointer to upstream state or large dataset."
    },
    "idempotencyKey": {
      "type": "string",
      "description": "Unique key ensuring at-most-once execution for mutating actions."
    },
    "options": {
      "type": "object",
      "properties": {
        "timeoutMs": { "type": "integer", "default": 10000, "maximum": 60000 },
        "executionMode": { "type": "string", "enum": ["SYNC", "ASYNC"], "default": "SYNC" },
        "compactResponse": { "type": "boolean", "default": true }
      }
    },
    "payload": {
      "type": "object",
      "description": "Domain-specific input parameters strictly conforming to the Capability's JSON Schema."
    }
  },
  "required": ["protocolVersion", "requestId", "capabilityId", "payload"]
}
```

### Valid Request Example
```json
{
  "protocolVersion": "1.0",
  "requestId": "4fa89a42-7a01-44ec-9620-33fae995a981",
  "correlationId": "thread_chat_20261109_9812",
  "capabilityId": "inventory.material.stock_in.approve@v1.0",
  "contextHandle": "ctx_hnd_89a01bf2:tenant_main",
  "idempotencyKey": "idem_stock_in_appr_10042",
  "options": {
    "timeoutMs": 5000,
    "executionMode": "SYNC",
    "compactResponse": true
  },
  "payload": {
    "ticketId": 10042,
    "approvalNotes": "Physical count verified against supplier delivery note."
  }
}
```

---

## 3. Response Envelope Specification

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "title": "UccpResponseEnvelope",
  "type": "object",
  "properties": {
    "protocolVersion": { "type": "string", "enum": ["1.0"] },
    "requestId": { "type": "string", "format": "uuid" },
    "correlationId": { "type": "string" },
    "status": {
      "type": "string",
      "enum": ["SUCCESS", "PENDING_APPROVAL", "RUNNING", "FAILED"]
    },
    "executionDurationMs": { "type": "number" },
    "contextHandle": {
      "type": "string",
      "description": "Generated or mutated opaque context reference for subsequent chained operations."
    },
    "summary": {
      "type": "string",
      "description": "Token-optimized natural language or tabular synthesis for LLM consumption."
    },
    "data": {
      "type": "object",
      "description": "Structured domain output matching the capability output schema."
    },
    "error": {
      "$ref": "#/$defs/UccpError"
    }
  },
  "required": ["protocolVersion", "requestId", "status"]
}
```

### Valid Response Example (Success with Summary & Handle)
```json
{
  "protocolVersion": "1.0",
  "requestId": "4fa89a42-7a01-44ec-9620-33fae995a981",
  "correlationId": "thread_chat_20261109_9812",
  "status": "SUCCESS",
  "executionDurationMs": 42.8,
  "contextHandle": "ctx_res_stock_audit_8819:tenant_main",
  "summary": "Ticket #10042 approved successfully. Inventory balance increased by 450 KG. Audit log #8819 created.",
  "data": {
    "ticketId": 10042,
    "status": "Approved",
    "approvedAt": "2026-11-09T11:27:00Z",
    "approvedBy": "phong.vo",
    "inventoryBalanceDelta": 450.0
  }
}
```

---

## 4. Error Envelope Specification (RFC 7807 + JSON-RPC Aligned)

UCCP models errors with strict categorization to enable autonomous client recovery:

```json
{
  "protocolVersion": "1.0",
  "requestId": "4fa89a42-7a01-44ec-9620-33fae995a981",
  "status": "FAILED",
  "error": {
    "code": "UCCP_403_APPROVAL_LIMIT_EXCEEDED",
    "category": "AUTHORIZATION",
    "retryable": false,
    "message": "User approval limit ($10,000) is lower than ticket total value ($14,500).",
    "details": {
      "requiredRole": "GeneralDirector",
      "currentLimit": 10000.0,
      "ticketValue": 14500.0
    },
    "remediation": "Request approval escalation from the General Director."
  }
}
```

### Standard Error Categories
- `VALIDATION` (`400`): Malformed schema or invalid arguments. Non-retryable.
- `AUTHENTICATION` (`401`): Missing or expired bearer token.
- `AUTHORIZATION` (`403`): Policy evaluation denied access. Non-retryable without privilege escalation.
- `NOT_FOUND` (`404`): Capability or target entity does not exist.
- `CONFLICT` (`409`): Idempotency violation or concurrent lock conflict.
- `RATE_LIMIT` (`429`): Resource quota exceeded. Retryable after backoff.
- `INTERNAL_FAULT` (`500`): Unhandled system exception.
- `DEADLINE_EXCEEDED` (`504`): Execution timeout exceeded.
