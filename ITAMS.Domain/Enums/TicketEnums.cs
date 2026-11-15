namespace ITAMS.Domain.Enums;

public enum TicketCategory { Hardware, Printer, Network, Software, Other }

/// <summary>How badly the reporter says it affects their work. Technicians set the real priority.</summary>
public enum TicketImpact { Blocking, Degraded, Minor }

public enum TicketPriority { Low, Normal, High, Critical }

public enum TicketStatus { New, InProgress, Resolved, Closed }

public enum TicketSource { PublicLink, StaffAccount }
