using System.Reflection;
using System.Text.Json;
using NoSuffering.Shops;
using NoSuffering.Multiplayer;
using NoSuffering.GameBridge;

var cloneEntry = typeof(ShopService).GetMethod("CloneEntry", BindingFlags.NonPublic | BindingFlags.Static)!;
ShopEntryRecord Clone(ShopEntryRecord entry) => (ShopEntryRecord)cloneEntry.Invoke(null, [entry])!;
void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
string Entries(ShopPlayerRecord player) => JsonSerializer.Serialize(player with { Entries = player.Entries.Select(Clone).ToArray() });

var lf = new ShopEntryRecord("{\n\"id\":\"CARD.A\",\"props\":{\"b\":2,\"a\":1},\"slots\":[1,2]\n}",
    "{\"id\":\"CARD.A\",\"upgrade\":1}", "{\"id\":\"RELIC.A\"}", 99, true,
    ["RELIC.A"], "character", CardPool: ["CARD.A", "CARD.B"], TemplateModel: "{\"id\":\"RELIC.A\"}");
var crlf = lf with { OriginalCard = "{\r\n\"slots\":[1,2],\"props\":{\"a\":1,\"b\":2},\"id\":\"CARD.A\"\r\n}" };
Require(JsonSerializer.Serialize(Clone(lf)) == JsonSerializer.Serialize(Clone(crlf)), "Whitespace/object-key order changed the snapshot.");
Require(Clone(lf).OriginalCard == "{\"id\":\"CARD.A\",\"props\":{\"a\":1,\"b\":2},\"slots\":[1,2]}", "Canonical payload lost a value.");
foreach (var changed in new[] { lf with { Cost = 100 }, lf with { OnSale = false },
    lf with { Card = "{\"id\":\"CARD.A\",\"upgrade\":2}" },
    lf with { OriginalCard = lf.OriginalCard!.Replace("[1,2]", "[2,1]") },
    lf with { CardPool = ["CARD.B", "CARD.A"] }, lf with { TemplateModel = "{\"id\":\"RELIC.B\"}" } })
    Require(JsonSerializer.Serialize(Clone(lf)) != JsonSerializer.Serialize(Clone(changed)), "Gameplay field difference was discarded.");
Require(lf.OriginalCard!.Contains('\n') && crlf.OriginalCard!.Contains("\r\n"), "Cloning modified source payloads.");

if (args.Length == 3 && args[0] == "--native")
{
    var normalize = typeof(HostCoordinator).GetMethod("PrepareNativeDigest", BindingFlags.NonPublic | BindingFlags.Static)!;
    var canonical = typeof(HostCoordinator).GetMethod("CanonicalJson", BindingFlags.NonPublic | BindingFlags.Static)!;
    string Prepare(string json)
    {
        var snapshot = GameBridge.Thaw(json);
        normalize.Invoke(null, [snapshot]);
        return (string)canonical.Invoke(null, [GameBridge.Freeze(snapshot)])!;
    }
    string host = File.ReadAllText(args[1]), client = File.ReadAllText(args[2]);
    Require(host != client && Prepare(host) == Prepare(client), "Actual purchase copies did not converge.");
    var root = System.Text.Json.Nodes.JsonNode.Parse(host)!;
    var owner = root["players"]![0]!;
    int gold = owner["gold"]!.GetValue<int>();
    owner["gold"] = gold + 1;
    Require(Prepare(root.ToJsonString()) != Prepare(host), "Real gold divergence was discarded.");
    root = System.Text.Json.Nodes.JsonNode.Parse(host)!;
    root["players"]![0]!["deck"]![0]!["id"] = "CARD.INFLAME";
    Require(Prepare(root.ToJsonString()) != Prepare(host), "Real deck divergence was discarded.");
    root = System.Text.Json.Nodes.JsonNode.Parse(host)!;
    root["players"]![0]!["potions"] = System.Text.Json.Nodes.JsonNode.Parse("[{\"id\":\"POTION.WEAK_POTION\",\"slot_index\":0}]");
    Require(Prepare(root.ToJsonString()) != Prepare(host), "Real potion inventory divergence was discarded.");
    root = System.Text.Json.Nodes.JsonNode.Parse(host)!;
    root["map_point_history"]![0]![3]!["player_stats"]![0]!["cards_gained"] =
        System.Text.Json.Nodes.JsonNode.Parse("[{\"id\":\"CARD.BLUDGEON\"}]");
    Require(Prepare(root.ToJsonString()) != Prepare(host), "Shop CardsGained divergence was discarded.");
    root = System.Text.Json.Nodes.JsonNode.Parse(host)!;
    var nonShop = root["map_point_history"]![0]![2]!["player_stats"]![0]!;
    nonShop["card_choices"] = System.Text.Json.Nodes.JsonNode.Parse("[{\"card\":{\"id\":\"CARD.INFLAME\"},\"was_picked\":true}]");
    Require(Prepare(root.ToJsonString()) != Prepare(host), "Non-shop picked history was discarded.");
    Require(File.ReadAllText(args[1]) == host && File.ReadAllText(args[2]) == client, "Source diagnostic files changed.");
    Console.WriteLine("PASS: actual native purchase copies converge; gold, deck, potions, CardsGained and non-shop picked choices remain compared; files unchanged.");
}
else if (args.Length == 2)
{
    var mac = JsonSerializer.Deserialize<ShopRecord>(File.ReadAllText(args[0]))!;
    var windows = JsonSerializer.Deserialize<ShopRecord>(File.ReadAllText(args[1]))!;
    Require(mac.Context == windows.Context && mac.Players.Keys.Order().SequenceEqual(windows.Players.Keys.Order()), "Captured context/players differ.");
    foreach (ulong id in mac.Players.Keys)
        Require(Entries(mac.Players[id]) == Entries(windows.Players[id]), $"Captured gameplay stock differs for actor {id}.");
    Console.WriteLine("PASS: actual Mac/Windows shop captures canonicalize identically for every actor.");
}
Console.WriteLine("PASS: whitespace/key order normalize; native values, array order, price, sale, pool and templates remain compared; sources unchanged.");
