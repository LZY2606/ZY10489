#pragma warning disable
using MessagePack;

namespace MasterMemory.Tests.TestStructures
{
    // Minimal schema for the generated-table / index / RangeView query-path tests.
    // One unique primary key, one unique secondary key (string, ordinal comparer),
    // one non-unique secondary index, plus a foreign key to PathItem.
    [MemoryTable("quest_path"), MessagePackObject(true)]
    public class QuestPath : IValidatable<QuestPath>
    {
        [PrimaryKey]
        public int QuestId { get; set; }
        [SecondaryKey(0)]
        public string Name { get; set; }
        [SecondaryKey(1), NonUnique]
        public int Level { get; set; }
        public string Category { get; set; }
        public int RewardItemId { get; set; }

        public void Validate(IValidator<QuestPath> validator)
        {
            validator.GetReferenceSet<PathItem>().Exists(x => x.RewardItemId, x => x.ItemId);
        }
    }

    [MemoryTable("path_item"), MessagePackObject(true)]
    public class PathItem
    {
        [PrimaryKey]
        public int ItemId { get; set; }
    }
}
