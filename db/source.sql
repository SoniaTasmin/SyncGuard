-- Source system schema (SyncGuardSource). Idempotent: safe to run on every startup.

IF OBJECT_ID('dbo.Orders') IS NULL
CREATE TABLE dbo.Orders (
    Id            INT IDENTITY  NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
    OrderNumber   NVARCHAR(50)  NOT NULL CONSTRAINT UQ_Orders_OrderNumber UNIQUE,
    CustomerCode  NVARCHAR(20)  NOT NULL,
    TotalAmount   DECIMAL(18,2) NOT NULL,
    CreatedAtUtc  DATETIME2(3)  NOT NULL
);
GO

IF OBJECT_ID('dbo.OrderLines') IS NULL
CREATE TABLE dbo.OrderLines (
    Id          INT IDENTITY  NOT NULL CONSTRAINT PK_OrderLines PRIMARY KEY,
    OrderId     INT           NOT NULL CONSTRAINT FK_OrderLines_Orders REFERENCES dbo.Orders(Id),
    LineNumber  INT           NOT NULL,
    Sku         NVARCHAR(30)  NOT NULL,
    Quantity    INT           NOT NULL,
    UnitPrice   DECIMAL(18,2) NOT NULL,
    CONSTRAINT UQ_OrderLines_Order_Line UNIQUE (OrderId, LineNumber)
);
GO

-- Transactional outbox. Id is the stable event id and is sent as the Idempotency-Key.
-- Status: 0 = Pending, 1 = Delivered, 2 = ManualReview.
IF OBJECT_ID('dbo.OutboxEvents') IS NULL
CREATE TABLE dbo.OutboxEvents (
    Id                UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_OutboxEvents PRIMARY KEY NONCLUSTERED,
    OrderId           INT              NOT NULL CONSTRAINT FK_OutboxEvents_Orders REFERENCES dbo.Orders(Id),
    EventType         NVARCHAR(50)     NOT NULL,
    Payload           NVARCHAR(MAX)    NOT NULL,
    Status            TINYINT          NOT NULL,
    AttemptCount      INT              NOT NULL,
    CreatedAtUtc      DATETIME2(3)     NOT NULL,
    NextAttemptAtUtc  DATETIME2(3)     NOT NULL,
    LastAttemptAtUtc  DATETIME2(3)     NULL,
    DeliveredAtUtc    DATETIME2(3)     NULL,
    LastStatusCode    INT              NULL,
    LastError         NVARCHAR(1000)   NULL,
    DestinationId     BIGINT           NULL,
    CONSTRAINT UQ_OutboxEvents_Order_Type UNIQUE (OrderId, EventType),
    CONSTRAINT CK_OutboxEvents_Status CHECK (Status IN (0, 1, 2))
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'CIX_OutboxEvents_CreatedAtUtc')
CREATE CLUSTERED INDEX CIX_OutboxEvents_CreatedAtUtc ON dbo.OutboxEvents (CreatedAtUtc);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_OutboxEvents_Due')
CREATE INDEX IX_OutboxEvents_Due ON dbo.OutboxEvents (Status, NextAttemptAtUtc);
GO

-- Single-row lease: guarantees one active dispatcher and doubles as the worker heartbeat.
IF OBJECT_ID('dbo.WorkerLeases') IS NULL
CREATE TABLE dbo.WorkerLeases (
    Name               NVARCHAR(50)  NOT NULL CONSTRAINT PK_WorkerLeases PRIMARY KEY,
    OwnerId            NVARCHAR(100) NULL,
    LeaseExpiresAtUtc  DATETIME2(3)  NOT NULL,
    HeartbeatAtUtc     DATETIME2(3)  NULL
);
GO

IF NOT EXISTS (SELECT 1 FROM dbo.WorkerLeases WHERE Name = 'outbox-dispatcher')
INSERT dbo.WorkerLeases (Name, OwnerId, LeaseExpiresAtUtc, HeartbeatAtUtc)
VALUES ('outbox-dispatcher', NULL, '2000-01-01', NULL);
GO
