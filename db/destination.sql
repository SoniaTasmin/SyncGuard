-- Mock destination (fulfillment) schema (SyncGuardDestination). Idempotent.

IF OBJECT_ID('dbo.ReceivedOrders') IS NULL
CREATE TABLE dbo.ReceivedOrders (
    Id              BIGINT IDENTITY NOT NULL CONSTRAINT PK_ReceivedOrders PRIMARY KEY,
    -- Durable idempotency: one row per key, whatever happens to responses.
    IdempotencyKey  NVARCHAR(100)   NOT NULL CONSTRAINT UQ_ReceivedOrders_IdempotencyKey UNIQUE,
    -- Business guard: an order number is booked at most once, even under a different key.
    OrderNumber     NVARCHAR(50)    NOT NULL CONSTRAINT UQ_ReceivedOrders_OrderNumber UNIQUE,
    CustomerCode    NVARCHAR(20)    NOT NULL,
    TotalAmount     DECIMAL(18,2)   NOT NULL,
    LinesJson       NVARCHAR(MAX)   NOT NULL,
    RequestHash     CHAR(64)        NOT NULL,
    ReceivedAtUtc   DATETIME2(3)    NOT NULL
);
GO
