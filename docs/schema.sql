IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    CREATE TABLE [AspNetRoles] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(256) NULL,
        [NormalizedName] nvarchar(256) NULL,
        [ConcurrencyStamp] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetRoles] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    CREATE TABLE [AspNetUsers] (
        [Id] uniqueidentifier NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UserName] nvarchar(256) NULL,
        [NormalizedUserName] nvarchar(256) NULL,
        [Email] nvarchar(256) NULL,
        [NormalizedEmail] nvarchar(256) NULL,
        [EmailConfirmed] bit NOT NULL,
        [PasswordHash] nvarchar(max) NULL,
        [SecurityStamp] nvarchar(max) NULL,
        [ConcurrencyStamp] nvarchar(max) NULL,
        [PhoneNumber] nvarchar(max) NULL,
        [PhoneNumberConfirmed] bit NOT NULL,
        [TwoFactorEnabled] bit NOT NULL,
        [LockoutEnd] datetimeoffset NULL,
        [LockoutEnabled] bit NOT NULL,
        [AccessFailedCount] int NOT NULL,
        CONSTRAINT [PK_AspNetUsers] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    CREATE TABLE [AuditEvents] (
        [Id] uniqueidentifier NOT NULL,
        [ActorId] uniqueidentifier NOT NULL,
        [Action] nvarchar(64) NOT NULL,
        [TargetId] uniqueidentifier NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_AuditEvents] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    CREATE TABLE [AspNetRoleClaims] (
        [Id] int NOT NULL IDENTITY,
        [RoleId] uniqueidentifier NOT NULL,
        [ClaimType] nvarchar(max) NULL,
        [ClaimValue] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetRoleClaims] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AspNetRoleClaims_AspNetRoles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [AspNetRoles] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    CREATE TABLE [AspNetUserClaims] (
        [Id] int NOT NULL IDENTITY,
        [UserId] uniqueidentifier NOT NULL,
        [ClaimType] nvarchar(max) NULL,
        [ClaimValue] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetUserClaims] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AspNetUserClaims_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    CREATE TABLE [AspNetUserLogins] (
        [LoginProvider] nvarchar(450) NOT NULL,
        [ProviderKey] nvarchar(450) NOT NULL,
        [ProviderDisplayName] nvarchar(max) NULL,
        [UserId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_AspNetUserLogins] PRIMARY KEY ([LoginProvider], [ProviderKey]),
        CONSTRAINT [FK_AspNetUserLogins_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    CREATE TABLE [AspNetUserRoles] (
        [UserId] uniqueidentifier NOT NULL,
        [RoleId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_AspNetUserRoles] PRIMARY KEY ([UserId], [RoleId]),
        CONSTRAINT [FK_AspNetUserRoles_AspNetRoles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [AspNetRoles] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_AspNetUserRoles_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    CREATE TABLE [AspNetUserTokens] (
        [UserId] uniqueidentifier NOT NULL,
        [LoginProvider] nvarchar(450) NOT NULL,
        [Name] nvarchar(450) NOT NULL,
        [Value] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetUserTokens] PRIMARY KEY ([UserId], [LoginProvider], [Name]),
        CONSTRAINT [FK_AspNetUserTokens_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    CREATE TABLE [Sessions] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [ExpiresAt] datetimeoffset NOT NULL,
        [RevokedAt] datetimeoffset NULL,
        CONSTRAINT [PK_Sessions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Sessions_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    CREATE TABLE [Wallets] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NULL,
        [Currency] varchar(3) NOT NULL,
        [Balance] decimal(19,2) NOT NULL,
        [IsSystem] bit NOT NULL,
        [IsFrozen] bit NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_Wallets] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Wallet_Balance] CHECK ([IsSystem] = 1 OR [Balance] >= 0),
        CONSTRAINT [CK_Wallet_Currency] CHECK ([Currency] = 'BDT'),
        CONSTRAINT [CK_Wallet_Owner] CHECK (([IsSystem] = 1 AND [UserId] IS NULL) OR ([IsSystem] = 0 AND [UserId] IS NOT NULL)),
        CONSTRAINT [FK_Wallets_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    CREATE TABLE [RefreshTokens] (
        [Id] uniqueidentifier NOT NULL,
        [SessionId] uniqueidentifier NOT NULL,
        [Hash] varchar(64) NOT NULL,
        [UsedAt] datetimeoffset NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_RefreshTokens] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_RefreshTokens_Sessions_SessionId] FOREIGN KEY ([SessionId]) REFERENCES [Sessions] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    CREATE TABLE [Transfers] (
        [Id] uniqueidentifier NOT NULL,
        [ActorId] uniqueidentifier NOT NULL,
        [SourceWalletId] uniqueidentifier NOT NULL,
        [DestinationWalletId] uniqueidentifier NOT NULL,
        [Amount] decimal(19,2) NOT NULL,
        [Currency] varchar(3) NOT NULL,
        [IdempotencyKey] varchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [RequestHash] varchar(64) NOT NULL,
        [Kind] nvarchar(24) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_Transfers] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Transfer_Amount] CHECK ([Amount] > 0),
        CONSTRAINT [CK_Transfer_Currency] CHECK ([Currency] = 'BDT'),
        CONSTRAINT [CK_Transfer_DifferentWallets] CHECK ([SourceWalletId] <> [DestinationWalletId]),
        CONSTRAINT [FK_Transfers_AspNetUsers_ActorId] FOREIGN KEY ([ActorId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Transfers_Wallets_DestinationWalletId] FOREIGN KEY ([DestinationWalletId]) REFERENCES [Wallets] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Transfers_Wallets_SourceWalletId] FOREIGN KEY ([SourceWalletId]) REFERENCES [Wallets] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    CREATE TABLE [LedgerEntries] (
        [Id] uniqueidentifier NOT NULL,
        [TransferId] uniqueidentifier NOT NULL,
        [WalletId] uniqueidentifier NOT NULL,
        [Debit] decimal(19,2) NOT NULL,
        [Credit] decimal(19,2) NOT NULL,
        [Currency] varchar(3) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_LedgerEntries] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Ledger_Currency] CHECK ([Currency] = 'BDT'),
        CONSTRAINT [CK_Ledger_OneSide] CHECK (([Debit] > 0 AND [Credit] = 0) OR ([Credit] > 0 AND [Debit] = 0)),
        CONSTRAINT [FK_LedgerEntries_Transfers_TransferId] FOREIGN KEY ([TransferId]) REFERENCES [Transfers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_LedgerEntries_Wallets_WalletId] FOREIGN KEY ([WalletId]) REFERENCES [Wallets] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    CREATE INDEX [IX_AspNetRoleClaims_RoleId] ON [AspNetRoleClaims] ([RoleId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [RoleNameIndex] ON [AspNetRoles] ([NormalizedName]) WHERE [NormalizedName] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    CREATE INDEX [IX_AspNetUserClaims_UserId] ON [AspNetUserClaims] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    CREATE INDEX [IX_AspNetUserLogins_UserId] ON [AspNetUserLogins] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    CREATE INDEX [IX_AspNetUserRoles_RoleId] ON [AspNetUserRoles] ([RoleId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [EmailIndex] ON [AspNetUsers] ([NormalizedEmail]) WHERE [NormalizedEmail] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UserNameIndex] ON [AspNetUsers] ([NormalizedUserName]) WHERE [NormalizedUserName] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    CREATE UNIQUE INDEX [IX_LedgerEntries_TransferId_WalletId] ON [LedgerEntries] ([TransferId], [WalletId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    CREATE INDEX [IX_LedgerEntries_WalletId_CreatedAt] ON [LedgerEntries] ([WalletId], [CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RefreshTokens_Hash] ON [RefreshTokens] ([Hash]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    CREATE INDEX [IX_RefreshTokens_SessionId] ON [RefreshTokens] ([SessionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    CREATE INDEX [IX_Sessions_UserId] ON [Sessions] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Transfers_ActorId_IdempotencyKey] ON [Transfers] ([ActorId], [IdempotencyKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    CREATE INDEX [IX_Transfers_DestinationWalletId_CreatedAt] ON [Transfers] ([DestinationWalletId], [CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    CREATE INDEX [IX_Transfers_SourceWalletId_CreatedAt] ON [Transfers] ([SourceWalletId], [CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_Wallets_UserId] ON [Wallets] ([UserId]) WHERE [UserId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025071227_InitialWalletLedger'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261025071227_InitialWalletLedger', N'10.0.13');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025154416_AccountSecurityAndOperations'
)
BEGIN
    ALTER TABLE [Sessions] ADD [CreatedAt] datetimeoffset NOT NULL DEFAULT '0001-01-01T00:00:00.0000000+00:00';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025154416_AccountSecurityAndOperations'
)
BEGIN
    ALTER TABLE [Sessions] ADD [SecurityStamp] nvarchar(128) NOT NULL DEFAULT N'';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025154416_AccountSecurityAndOperations'
)
BEGIN
    ALTER TABLE [AuditEvents] ADD [Reason] nvarchar(500) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025154416_AccountSecurityAndOperations'
)
BEGIN
    CREATE TABLE [AccountChallenges] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [Purpose] nvarchar(32) NOT NULL,
        [TokenHash] varchar(64) NOT NULL,
        [SecurityStamp] nvarchar(128) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [ExpiresAt] datetimeoffset NOT NULL,
        [UsedAt] datetimeoffset NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_AccountChallenges] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AccountChallenges_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025154416_AccountSecurityAndOperations'
)
BEGIN
    CREATE TABLE [EmailOutbox] (
        [Id] uniqueidentifier NOT NULL,
        [ProtectedPayload] nvarchar(max) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [ExpiresAt] datetimeoffset NOT NULL,
        [NextAttemptAt] datetimeoffset NOT NULL,
        [Attempts] int NOT NULL,
        [LeaseId] uniqueidentifier NULL,
        [LeaseUntil] datetimeoffset NULL,
        [SentAt] datetimeoffset NULL,
        [FailedAt] datetimeoffset NULL,
        [LastErrorType] nvarchar(128) NULL,
        CONSTRAINT [PK_EmailOutbox] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025154416_AccountSecurityAndOperations'
)
BEGIN
    CREATE UNIQUE INDEX [IX_AccountChallenges_TokenHash] ON [AccountChallenges] ([TokenHash]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025154416_AccountSecurityAndOperations'
)
BEGIN
    CREATE INDEX [IX_AccountChallenges_UserId_Purpose_CreatedAt] ON [AccountChallenges] ([UserId], [Purpose], [CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025154416_AccountSecurityAndOperations'
)
BEGIN
    CREATE INDEX [IX_EmailOutbox_SentAt_FailedAt_NextAttemptAt] ON [EmailOutbox] ([SentAt], [FailedAt], [NextAttemptAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025154416_AccountSecurityAndOperations'
)
BEGIN
    UPDATE [Sessions] SET [RevokedAt] = SYSUTCDATETIME() WHERE [RevokedAt] IS NULL
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025154416_AccountSecurityAndOperations'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261025154416_AccountSecurityAndOperations', N'10.0.13');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026041948_MfaAndWalletApprovals'
)
BEGIN
    ALTER TABLE [Wallets] ADD [StateVersion] bigint NOT NULL DEFAULT CAST(0 AS bigint);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026041948_MfaAndWalletApprovals'
)
BEGIN
    ALTER TABLE [Sessions] ADD [AuthenticationMethod] nvarchar(16) NOT NULL DEFAULT N'';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026041948_MfaAndWalletApprovals'
)
BEGIN
    ALTER TABLE [Sessions] ADD [MfaVerifiedAt] datetimeoffset NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026041948_MfaAndWalletApprovals'
)
BEGIN
    CREATE TABLE [MfaProfiles] (
        [UserId] uniqueidentifier NOT NULL,
        [ProtectedSecret] nvarchar(max) NOT NULL,
        [EnrollmentExpiresAt] datetimeoffset NULL,
        [LastAcceptedStep] bigint NOT NULL,
        [FailedAttempts] int NOT NULL,
        [LockedUntil] datetimeoffset NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_MfaProfiles] PRIMARY KEY ([UserId]),
        CONSTRAINT [FK_MfaProfiles_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026041948_MfaAndWalletApprovals'
)
BEGIN
    CREATE TABLE [MfaRecoveryCodes] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [Hash] varchar(64) NOT NULL,
        [UsedAt] datetimeoffset NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_MfaRecoveryCodes] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_MfaRecoveryCodes_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026041948_MfaAndWalletApprovals'
)
BEGIN
    CREATE TABLE [WalletApprovals] (
        [Id] uniqueidentifier NOT NULL,
        [WalletId] uniqueidentifier NOT NULL,
        [WalletStateVersion] bigint NOT NULL,
        [RequestedBy] uniqueidentifier NOT NULL,
        [RequesterSecurityStamp] nvarchar(128) NOT NULL,
        [RequestedAt] datetimeoffset NOT NULL,
        [ExpiresAt] datetimeoffset NOT NULL,
        [Reason] nvarchar(500) NOT NULL,
        [Status] nvarchar(24) NOT NULL,
        [ReviewedBy] uniqueidentifier NULL,
        [ReviewedAt] datetimeoffset NULL,
        [ReviewReason] nvarchar(500) NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_WalletApprovals] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Approval_SeparateReviewer] CHECK ([Status] <> 'Approved' OR ([ReviewedBy] IS NOT NULL AND [ReviewedBy] <> [RequestedBy])),
        CONSTRAINT [CK_Approval_Status] CHECK ([Status] IN ('Pending','Approved','Rejected','Cancelled','Expired','Invalidated')),
        CONSTRAINT [FK_WalletApprovals_AspNetUsers_RequestedBy] FOREIGN KEY ([RequestedBy]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_WalletApprovals_AspNetUsers_ReviewedBy] FOREIGN KEY ([ReviewedBy]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_WalletApprovals_Wallets_WalletId] FOREIGN KEY ([WalletId]) REFERENCES [Wallets] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026041948_MfaAndWalletApprovals'
)
BEGIN
    CREATE UNIQUE INDEX [IX_MfaRecoveryCodes_UserId_Hash] ON [MfaRecoveryCodes] ([UserId], [Hash]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026041948_MfaAndWalletApprovals'
)
BEGIN
    CREATE INDEX [IX_WalletApprovals_RequestedBy] ON [WalletApprovals] ([RequestedBy]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026041948_MfaAndWalletApprovals'
)
BEGIN
    CREATE INDEX [IX_WalletApprovals_ReviewedBy] ON [WalletApprovals] ([ReviewedBy]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026041948_MfaAndWalletApprovals'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_WalletApprovals_WalletId] ON [WalletApprovals] ([WalletId]) WHERE [Status] = ''Pending''');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026041948_MfaAndWalletApprovals'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261026041948_MfaAndWalletApprovals', N'10.0.13');
END;

COMMIT;
GO

