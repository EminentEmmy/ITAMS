namespace ITAMS.Domain.Enums;

public enum UserRole
{
    SuperAdmin = 0,   // Vendor / platform owner
    TenantAdmin = 1,  // e.g. NAHCO IT Director
    Technician = 2,   // Field IT engineer
    Auditor = 3       // Read-only
}

public enum AssetType
{
    SystemUnit,
    BoardingPrinter,
    CargoScaleInterface,
    Monitor,
    Other
}

public enum AssetStatus
{
    InStock,
    Deployed,
    UnderRepair,
    Decommissioned
}
