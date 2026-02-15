using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace BTCPayServer.Plugins.Payjoin.Data;

public class PluginData
{
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public string Id { get; set; }

    public DateTimeOffset Timestamp { get; set; }
}
