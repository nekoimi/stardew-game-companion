using System.Globalization;
using System.Xml;
using System.Xml.Serialization;
using StardewValley;

namespace StardewAI.Companion.Mod.Domain;

public sealed record NativeInventorySlot(int SlotIndex, string ItemXml);

/// <summary>Preserves the game's item save fields, including derived item state.
/// Failure must abort the save/load instead of silently replacing an item with its summary.</summary>
internal static class NativeInventoryPersistence
{
    private static readonly XmlSerializer Serializer = new(typeof(Item));

    public static List<NativeInventorySlot> Capture(IList<Item> items)
    {
        var slots = new List<NativeInventorySlot>();
        for (int slot = 0; slot < items.Count; slot++)
        {
            if (items[slot] is not { } item) continue;
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            Serializer.Serialize(writer, item);
            slots.Add(new NativeInventorySlot(slot, writer.ToString()));
        }
        return slots;
    }

    public static Item?[] Restore(IReadOnlyList<NativeInventorySlot> saved, int capacity = 36)
    {
        var slots = new Item?[capacity];
        var occupied = new HashSet<int>();
        foreach (var entry in saved)
        {
            if (entry.SlotIndex < 0 || entry.SlotIndex >= capacity || !occupied.Add(entry.SlotIndex))
                throw new InvalidOperationException($"Invalid or duplicate native inventory slot {entry.SlotIndex}.");
            using var text = new StringReader(entry.ItemXml);
            using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            slots[entry.SlotIndex] = Serializer.Deserialize(reader) as Item
                ?? throw new InvalidOperationException($"Native inventory slot {entry.SlotIndex} contains no item.");
        }
        return slots;
    }
}
