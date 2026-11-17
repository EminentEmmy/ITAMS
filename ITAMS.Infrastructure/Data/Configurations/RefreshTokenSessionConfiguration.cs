using ITAMS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ITAMS.Infrastructure.Data.Configurations;

/// <summary>Index for the per-request session check and the "your sessions" list.</summary>
public class RefreshTokenSessionConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> e) => e.HasIndex(t => t.SessionId);
}
