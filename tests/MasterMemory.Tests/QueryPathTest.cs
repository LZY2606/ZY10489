// Query-path tests for generated tables, indexes and RangeView.
// All data is fixed (no random sources) and databases are built with the
// default maxDegreeOfParallelism = 1 (sequential Init) to keep results stable.
using MasterMemory.Tests.TestStructures;
using MessagePack;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace MasterMemory.Tests
{
    public class QueryPathTest
    {
        readonly ITestOutputHelper output;

        public QueryPathTest(ITestOutputHelper output)
        {
            this.output = output;
            MessagePackSerializer.DefaultOptions = MessagePackSerializer.DefaultOptions.WithResolver(MessagePackResolver.Instance);
        }

        // Fixed data, intentionally appended out of primary-key order.
        // Level = 2 is shared by QuestId 30/40/50 (duplicate key on the non-unique index).
        static QuestPath[] CreateQuests() => new[]
        {
            new QuestPath { QuestId = 30, Name = "gamma", Level = 2, Category = "common", RewardItemId = 100 },
            new QuestPath { QuestId = 10, Name = "alpha", Level = 1, Category = "common", RewardItemId = 100 },
            new QuestPath { QuestId = 50, Name = "Zulu",  Level = 2, Category = "rare",   RewardItemId = 200 },
            new QuestPath { QuestId = 20, Name = "beta",  Level = 3, Category = "common", RewardItemId = 300 },
            new QuestPath { QuestId = 40, Name = "delta", Level = 2, Category = "rare",   RewardItemId = 300 },
        };

        static PathItem[] CreateItems() => new[]
        {
            new PathItem { ItemId = 300 },
            new PathItem { ItemId = 100 },
            new PathItem { ItemId = 200 },
        };

        static MemoryDatabase CreateDatabase(QuestPath[] quests, PathItem[] items, bool internString = true)
        {
            var builder = new DatabaseBuilder();
            if (quests != null) builder.Append(quests);
            if (items != null) builder.Append(items);
            var bin = builder.Build();
            return new MemoryDatabase(bin, internString: internString);
        }

        static int[] Ids(RangeView<QuestPath> view) => view.Select(x => x.QuestId).ToArray();

        [Fact]
        public void PrimaryKeyLookup_ReturnOrderIsPrimaryKeyOrder()
        {
            var db = CreateDatabase(CreateQuests(), CreateItems());
            var table = db.QuestPathTable;

            // Append-time sort: the stored array is ordered by primary key.
            Ids(table.All).ShouldBe(new[] { 10, 20, 30, 40, 50 });
            table.GetRawDataUnsafe().Select(x => x.QuestId).ShouldBe(new[] { 10, 20, 30, 40, 50 });
            Ids(table.AllReverse).ShouldBe(new[] { 50, 40, 30, 20, 10 });

            table.FindByQuestId(20).Name.ShouldBe("beta");
            table.TryFindByQuestId(50, out var hit).ShouldBeTrue();
            hit.Name.ShouldBe("Zulu");
            table.TryFindByQuestId(999, out _).ShouldBeFalse();
            Assert.Throws<KeyNotFoundException>(() => table.FindByQuestId(999));

            // Closest snaps inward on the sorted primary keys.
            table.FindClosestByQuestId(25, selectLower: true).QuestId.ShouldBe(20);
            table.FindClosestByQuestId(25, selectLower: false).QuestId.ShouldBe(30);

            output.WriteLine("All: " + string.Join(",", Ids(table.All)));
        }

        [Fact]
        public void UniqueSecondaryKey_UsesOrdinalStringComparer()
        {
            var db = CreateDatabase(CreateQuests(), CreateItems());
            var table = db.QuestPathTable;

            table.FindByName("delta").QuestId.ShouldBe(40);

            // Ordinal comparer (generation-time choice): 'Z'(90) sorts before 'a'(97).
            table.SortByName.Select(x => x.Name).ShouldBe(new[] { "Zulu", "alpha", "beta", "delta", "gamma" });

            // Ordinal is case-sensitive: no fallback to case-insensitive matching.
            Assert.Throws<KeyNotFoundException>(() => table.FindByName("zulu"));
            table.TryFindByName("ZULU", out _).ShouldBeFalse();
        }

        [Fact]
        public void NonUniqueIndex_ReturnsRangeViewSlice()
        {
            var db = CreateDatabase(CreateQuests(), CreateItems());
            var table = db.QuestPathTable;

            var view = table.FindByLevel(2);
            view.Count.ShouldBe(3);
            // Array.Sort is unstable, so only the set and the sort key are specified for duplicates.
            view.Select(x => x.QuestId).OrderBy(x => x).ShouldBe(new[] { 30, 40, 50 });
            view.All(x => x.Level == 2).ShouldBeTrue();
            view.Select(x => x.Level).ShouldBe(new[] { 2, 2, 2 });

            // No match -> empty view, not an exception.
            table.FindByLevel(99).Count.ShouldBe(0);

            // SortByLevel exposes the whole secondary index as a view.
            table.SortByLevel.Select(x => x.Level).ShouldBe(new[] { 1, 2, 2, 2, 3 });
            table.SortByLevel.Count.ShouldBe(5);

            output.WriteLine("FindByLevel(2): " + string.Join(",", Ids(view)));
            output.WriteLine("SortByLevel: " + string.Join(",", Ids(table.SortByLevel)));
        }

        [Fact]
        public void RangeQueries_RespectSliceBoundaries()
        {
            var db = CreateDatabase(CreateQuests(), CreateItems());
            var table = db.QuestPathTable;

            // Inclusive bounds on the unique primary key.
            var mid = table.FindRangeByQuestId(20, 40);
            Ids(mid).ShouldBe(new[] { 20, 30, 40 });
            mid.First.QuestId.ShouldBe(20);
            mid.Last.QuestId.ShouldBe(40);
            mid[0].QuestId.ShouldBe(20);
            mid[2].QuestId.ShouldBe(40);
            Assert.Throws<ArgumentOutOfRangeException>(() => mid[-1]);
            Assert.Throws<ArgumentOutOfRangeException>(() => mid[3]);

            // Descendant view iterates the same slice in reverse.
            Ids(table.FindRangeByQuestId(20, 40, ascendant: false)).ShouldBe(new[] { 40, 30, 20 });
            Ids(mid.Reverse).ShouldBe(new[] { 40, 30, 20 });

            // Bounds snap inward to the closest existing keys.
            Ids(table.FindRangeByQuestId(15, 35)).ShouldBe(new[] { 20, 30 });

            // Single element slice.
            var single = table.FindRangeByQuestId(10, 10);
            single.Count.ShouldBe(1);
            single.First.QuestId.ShouldBe(10);
            single.Last.QuestId.ShouldBe(10);

            // Out-of-data ranges and inverted bounds produce empty views.
            table.FindRangeByQuestId(0, 5).Count.ShouldBe(0);
            table.FindRangeByQuestId(100, 200).Count.ShouldBe(0);
            table.FindRangeByLevel(3, 1).Count.ShouldBe(0); // min > max
            table.FindRangeByLevel(0, 0).Count.ShouldBe(0);

            // Empty views still throw on element access.
            Assert.Throws<ArgumentOutOfRangeException>(() => table.FindRangeByQuestId(0, 5)[0]);
        }

        [Fact]
        public void RangeView_DoesNotCopyData()
        {
            var db = CreateDatabase(CreateQuests(), CreateItems());
            var table = db.QuestPathTable;

            var viewArrayField = typeof(RangeView<QuestPath>).GetField("orderedData", BindingFlags.NonPublic | BindingFlags.Instance);
            viewArrayField.ShouldNotBeNull();

            // All wraps the primary data array itself.
            var allArray = (QuestPath[])viewArrayField.GetValue(table.All);
            allArray.ShouldBeSameAs(table.GetRawDataUnsafe());

            // Secondary index views wrap the index array built at table construction,
            // which is a clone of (not the same array as) the primary data.
            var indexField = table.GetType().GetField("secondaryIndex1", BindingFlags.NonPublic | BindingFlags.Instance);
            indexField.ShouldNotBeNull();
            var indexArray = (QuestPath[])indexField.GetValue(table);

            var sliceArray = (QuestPath[])viewArrayField.GetValue(table.FindByLevel(2));
            sliceArray.ShouldBeSameAs(indexArray);
            sliceArray.ShouldNotBeSameAs(table.GetRawDataUnsafe());

            // Elements are shared references: no per-element copy either.
            table.All[0].ShouldBeSameAs(table.GetRawDataUnsafe()[0]);
            table.FindByLevel(2).First.ShouldBeSameAs(table.SortByLevel.First(x => x.Level == 2));
        }

        [Fact]
        public void StringInterning_IsControlledByConstructorOption()
        {
            var bin = new DatabaseBuilder().Append(CreateQuests()).Append(CreateItems()).Build();

            var interned = new MemoryDatabase(bin, internString: true);
            var i1 = interned.QuestPathTable.FindByQuestId(10).Category;
            var i2 = interned.QuestPathTable.FindByQuestId(20).Category;
            i1.ShouldBe("common");
            i1.ShouldBeSameAs(i2); // string.Intern during deserialization

            var plain = new MemoryDatabase(bin, internString: false);
            var p1 = plain.QuestPathTable.FindByQuestId(10).Category;
            var p2 = plain.QuestPathTable.FindByQuestId(20).Category;
            p1.ShouldBe("common");
            p1.ShouldNotBeSameAs(p2);
        }

        [Fact]
        public void EmptyTable_AndMissingTable_BehaveAsEmpty()
        {
            // Table appended with zero rows.
            var dbEmpty = CreateDatabase(new QuestPath[0], CreateItems());
            var empty = dbEmpty.QuestPathTable;
            empty.Count.ShouldBe(0);
            empty.All.Any().ShouldBeFalse();
            empty.TryFindByQuestId(10, out _).ShouldBeFalse();
            Assert.Throws<KeyNotFoundException>(() => empty.FindByQuestId(10));
            empty.FindRangeByQuestId(0, 100).Count.ShouldBe(0);
            empty.FindByLevel(2).Count.ShouldBe(0);
            empty.SortByLevel.Count.ShouldBe(0);
            Assert.Throws<ArgumentOutOfRangeException>(() => empty.All[0]);

            // Table not appended at all: ExtractTableData falls back to Array.Empty.
            var dbMissing = CreateDatabase(null, CreateItems());
            dbMissing.QuestPathTable.Count.ShouldBe(0);
            dbMissing.QuestPathTable.All.Any().ShouldBeFalse();
            dbMissing.QuestPathTable.FindByLevel(2).Count.ShouldBe(0);
            dbMissing.PathItemTable.Count.ShouldBe(3);
            IdsOfItems(dbMissing).ShouldBe(new[] { 100, 200, 300 });
        }

        static int[] IdsOfItems(MemoryDatabase db) => db.PathItemTable.All.Select(x => x.ItemId).ToArray();

        [Fact]
        public void DuplicatePrimaryKey_PublishSucceeds_RuntimeValidateReports()
        {
            var dup = CreateQuests().Concat(new[]
            {
                new QuestPath { QuestId = 10, Name = "duplicated", Level = 1, Category = "common", RewardItemId = 100 },
            }).ToArray();

            // Publish (Append/Build/load) does not enforce uniqueness.
            var db = CreateDatabase(dup, CreateItems());
            db.QuestPathTable.Count.ShouldBe(6);

            // Lookup still works but only "a" match is specified, not which one.
            db.QuestPathTable.FindByQuestId(10).QuestId.ShouldBe(10);

            // Uniqueness is a runtime check: Validate() reports a stable message and location.
            var result = db.Validate();
            result.IsValidationFailed.ShouldBeTrue();
            result.FailedResults.Count.ShouldBe(1);
            result.FailedResults[0].Message.ShouldBe("Unique failed: QuestId, value = 10");
            result.FailedResults[0].Type.ShouldBe(typeof(QuestPath));
            ((QuestPath)result.FailedResults[0].Data).QuestId.ShouldBe(10);
        }

        [Fact]
        public void ReferenceFailure_ReportsStableMessageAndLocation()
        {
            var quests = CreateQuests();
            quests[3].RewardItemId = 999; // QuestId = 20 references a missing item.

            var db = CreateDatabase(quests, CreateItems());
            var result = db.Validate();

            result.IsValidationFailed.ShouldBeTrue();
            result.FailedResults.Count.ShouldBe(1);
            result.FailedResults[0].Message.ShouldBe("Exists failed: QuestPath.RewardItemId -> PathItem.ItemId, value = 999, PK(QuestId) = 20");
            result.FailedResults[0].Type.ShouldBe(typeof(QuestPath));
            ((QuestPath)result.FailedResults[0].Data).QuestId.ShouldBe(20);

            output.WriteLine(result.FormatFailedResults());
        }

        [Fact]
        public void GeneratedApiSurface_MatchesKeyShapes()
        {
            var table = typeof(Tables.QuestPathTable);

            // Unique keys return a single element; non-unique keys return RangeView.
            table.GetMethod("FindByQuestId").ReturnType.ShouldBe(typeof(QuestPath));
            table.GetMethod("TryFindByQuestId").ReturnType.ShouldBe(typeof(bool));
            table.GetMethod("FindClosestByQuestId").ReturnType.ShouldBe(typeof(QuestPath));
            table.GetMethod("FindRangeByQuestId").ReturnType.ShouldBe(typeof(RangeView<QuestPath>));
            table.GetMethod("FindByName").ReturnType.ShouldBe(typeof(QuestPath));
            table.GetMethod("FindByLevel").ReturnType.ShouldBe(typeof(RangeView<QuestPath>));
            table.GetMethod("FindClosestByLevel").ReturnType.ShouldBe(typeof(RangeView<QuestPath>));
            table.GetProperty("SortByName").PropertyType.ShouldBe(typeof(RangeView<QuestPath>));
            table.GetProperty("SortByLevel").PropertyType.ShouldBe(typeof(RangeView<QuestPath>));

            // Builder publishes one Append overload per table type.
            typeof(DatabaseBuilder).GetMethods()
                .Count(x => x.Name == "Append" && x.GetParameters()[0].ParameterType == typeof(IEnumerable<QuestPath>))
                .ShouldBe(1);

            foreach (var name in new[] { "FindByQuestId", "FindByName", "FindByLevel", "FindRangeByQuestId", "SortByLevel" })
            {
                output.WriteLine(name + " -> " + (table.GetMethod(name)?.ReturnType.Name ?? table.GetProperty(name)?.PropertyType.Name));
            }
        }
    }
}
