namespace ITAMS.Api.Dtos;

public record NotificationItemDto(int TicketId, string Kind, string Title, string Text, DateTime WhenUtc, bool Unread);

public record NotificationsDto(int Unread, IReadOnlyList<NotificationItemDto> Items);
