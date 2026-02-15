using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BTCPayServer.Plugins.Template.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BTCPayServer.Plugins.Template;

public class PayjoinPluginDbContext : DbContext
{
    private readonly bool _designTime;

    public PayjoinPluginDbContext(DbContextOptions<PayjoinPluginDbContext> options, bool designTime = false)
        : base(options)
    {
        _designTime = designTime;
    }

    public DbSet<PluginData> PluginRecords { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("BTCPayServer.Plugins.Template");
    }
}
