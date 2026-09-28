# MasterMemory：生成表、索引与 RangeView 的查询路径分析

本文追踪一条查询从代码生成到运行时返回的完整调用链：生成器读取 attribute → 生成存储与索引 →
数据库构造（发布 / 加载）→ 范围查询。每个关键不变量附源码位置与对应验证测试。

验证用最小 schema（主键 + 唯一二级键 + 非唯一索引 + 外键引用）与固定数据见
`tests/MasterMemory.Tests/TestStructures/QuestPathMaster.cs`，测试见
`tests/MasterMemory.Tests/QueryPathTest.cs`。数据为固定常量、无随机源；数据库构造使用默认
`maxDegreeOfParallelism = 1`（顺序 `InitSequential`），避免并发调度带来的间歇性回归。

## 1. 调用链

### 1.1 生成器读取 attribute（编译期）

1. `MasterMemoryGenerator.Initialize`（`src/MasterMemory.SourceGenerator/MasterMemoryGenerator.cs:11`）
   通过 `ForAttributeWithMetadataName("MasterMemory.MemoryTableAttribute")`（同文件 :36）收集所有

   `[MemoryTable]` 类型。
2. `CodeGenerator.CreateGenerationContext`（`src/MasterMemory.SourceGenerator/GeneratorCore/CodeGenerator.cs:12`）
   逐属性调用 `ExtractPropertyAttribute`（同文件 :73），解析 `PrimaryKey` / `SecondaryKey(indexNo, keyOrder)` /
   `NonUnique` / `StringComparisonOption`；`AggregatePrimaryKey`（:189）与 `AggregateSecondaryKey`（:208）
   按 `KeyOrder` 组合复合键。缺少主键时报告诊断并跳过（:34-39）。
3. 比较器在**生成期**选定并烧进生成代码：`KeyBase.BuildComparer`
   （`src/MasterMemory.SourceGenerator/GeneratorCore/GenerationContext.cs:102`）——
   字符串键默认 `StringComparer.Ordinal`（可被 `StringComparisonOption` 覆盖），其余键用
   `Comparer<TKey>.Default`。int 型唯一主键走内联二分（`CanInlineBinarySearch`，同文件 :196）。
4. `EmitMemoryTable`（`MasterMemoryGenerator.cs:68`）渲染 5 个模板：`DatabaseBuilder`、
   `ImmutableBuilder`、`MemoryDatabase`、`MasterMemoryResolver` 以及每张表的 `{Class}Table`。

### 1.2 生成存储与索引（编译期产物）

- 每张表生成 `{Class}Table : TableBase<T>, ITableUniqueValidate`
  （`src/MasterMemory.SourceGenerator/GeneratorCore/TableTemplate.tt:14`）。
- **主键不建额外数组**：`PrimaryKey.TableName` 是 `"data"`
  （`src/MasterMemory.SourceGenerator/GeneratorCore/GenerationContext.cs` 的 `PrimaryKey` 记录），
  主键查询直接落在反序列化得到的 `data` 数组上，因此主键有序性必须由发布期保证（见 1.3）。
- **二级索引在表构造时现场排序**：生成的表构造函数对每个二级索引调用
  `CloneAndSortBy(selector, comparer)`（`TableTemplate.tt:30` → `src/MasterMemory/TableBase.cs:41`），
  即每次加载数据库都复制一份元素数组并按索引键排序。这是**运行期**行为，不依赖任何隐藏全局状态，
  只依赖构造函数参数 `sortedData`。
- 生成的查询 API 形态由键形状决定（`GenerationContext.cs` 的 `BuildFindPrefix` / `BuildReturnTypeName`）：
  唯一键 → `FindByX` 返回 `T`、`TryFindByX` 返回 `bool`；非唯一键（`[NonUnique]`）→ `FindByX` 返回
  `RangeView<T>`；每个键都有 `FindClosestByX` / `FindRangeByX`；每个二级索引有 `SortByX` 属性。
  该形态由 `QueryPathTest.GeneratedApiSurface_MatchesKeyShapes` 用反射锁定。

### 1.3 数据库构造（发布期 + 加载期）

发布（builder → 二进制）：

1. 生成的 `DatabaseBuilder.Append(dataSource)`（`src/MasterMemory.SourceGenerator/GeneratorCore/DatabaseBuilderTemplate.tt:20`）
   转发到 `DatabaseBuilderBase.AppendCore`（`src/MasterMemory/DatabaseBuilderBase.cs:39`）。
2. `AppendCore` 先调用 `FastSort`（同文件 :63）**按主键排序**——主键索引的排序发生在发布期、且仅在这里；
   随后 MessagePack 序列化并记录 `header[表名] = (offset, count)`。`Build()`（:97）/
   `WriteToStream`（:106）输出 header + 数据段。
3. `AppendCore` **不做唯一性检查**，重复主键可以正常发布（见 2.2）。

加载（二进制 → 不可变查询结构）：

1. `MemoryDatabaseBase` 构造函数（`src/MasterMemory/MemoryDatabaseBase.cs:20`）读 header；
   `internString: true`（默认）时用 `InternStringResolver` 包装 resolver
   （`src/MasterMemory/Internal/InternStringResolver.cs:8`，反序列化字符串经 `string.Intern`，:35）。
2. 生成的 `Init`（`src/MasterMemory.SourceGenerator/GeneratorCore/MemoryDatabaseTemplate.tt:36`）
   按 `maxDegreeOfParallelism` 分派 `InitSequential`（:48）或 `InitParallel`（:55，
   `Parallel.Invoke`）。默认 1 = 顺序，测试只走顺序路径。
3. 每张表经 `ExtractTableData`（`MemoryDatabaseBase.cs:39`）切片反序列化为 `T[]`，再
   `new {Class}Table(xs)`——二级索引的 `CloneAndSortBy` 在此刻执行。header 中缺失的表回退为
   `Array.Empty<T>()`（:48-52），即空表语义。

### 1.4 范围查询（运行期）

- 唯一键范围：生成的 `FindRangeByX` → `TableBase.FindUniqueRangeCore`
  （`src/MasterMemory/TableBase.cs:133`）→ `BinarySearch.FindClosest`
  （`src/MasterMemory/Internal/BinarySearch.cs:59`）两端各自向内吸附到最近存在键。
- 非唯一键等值：生成的 `FindByX` → `TableBase.FindManyCore`（`TableBase.cs:146`）→
  `BinarySearch.LowerBound` / `UpperBound`（`BinarySearch.cs:89` / :118）取得重复键的闭区间。
- 非唯一键范围：`TableBase.FindManyRangeCore`（`TableBase.cs:167`）→
  `LowerBoundClosest` / `UpperBoundClosest`（`BinarySearch.cs:152` / :187）；
  `min > max` 直接返回 `RangeView.Empty`（`TableBase.cs:172-173`）。
- 三者最终都只是 `new RangeView<T>(indexArray, lo, hi, ascendant)`
  （`src/MasterMemory/RangeView.cs:47`）：**不复制数据**，视图持有索引数组引用与 `[left, right]`
  边界；`hasValue` 判定（`RangeView.cs:49`）使 `left > right`、空数组、`null` 都归一为
  `Count == 0` 的空视图。

## 2. 关键不变量（生成期保证 vs 运行期检查）

### 2.1 索引排序发生在何时

| 索引 | 排序时机 | 位置 | 性质 |
| --- | --- | --- | --- |
| 主键（`data` 数组本身） | 发布期 `AppendCore` → `FastSort` | `DatabaseBuilderBase.cs:39`, `:63` | 生成期/发布期保证 |
| 各二级索引（`secondaryIndexN`） | 加载期表构造函数 `CloneAndSortBy` | `TableTemplate.tt:30` → `TableBase.cs:41` | 运行期、每次加载重建 |

验证：`QueryPathTest.PrimaryKeyLookup_ReturnOrderIsPrimaryKeyOrder` 断言乱序输入（30,10,50,20,40）
发布后 `All` 与 `GetRawDataUnsafe()` 均为 10,20,30,40,50；
`QueryPathTest.NonUniqueIndex_ReturnsRangeViewSlice` 断言 `SortByLevel` 按键非降序。
两者都只通过公开 API 观察，不依赖隐藏全局状态。

取舍：维持现状。主键排序放在发布期可避免每次加载都排序主数组；二级索引无法复用该时机
（一份数据只能按一个键物理排序），因此在表构造时复制并排序是唯一可行点。备选方案
（加载期也跳过二级排序、改为查询期排序）会牺牲 `RangeView` 的 O(log n) 边界定位，不采纳。

### 2.2 唯一性验证 vs 数据库发布

- 发布期：`AppendCore` 只排序不验证，重复主键可以 `Build()` 并正常加载——这是稳定结果而非异常。
- 运行期：`MemoryDatabase.Validate()`（生成于 `MemoryDatabaseTemplate.tt`）→
  生成的 `ITableUniqueValidate.ValidateUnique`（`TableTemplate.tt:117`）→
  `TableBase.ValidateUniqueCore`（`TableBase.cs:26`）用 `HashSet` 报告重复，
  错误位置信息为 `FaildItem{ Type = 元素类型, Message = "Unique failed: {键}, value = {值}", Data = 出错行 }`
  （`src/MasterMemory/Validation/ValidateResult.cs`）。
- 重复主键下 `FindBy` 仍能返回，但只保证返回"某一个"匹配（二分命中即返回，`BinarySearch.cs:8`），
  不保证是哪一个。

验证：`QueryPathTest.DuplicatePrimaryKey_PublishSucceeds_RuntimeValidateReports`——
重复 `QuestId = 10` 发布成功、`Validate()` 恰好报 1 条 `Unique failed: QuestId, value = 10`，
且 `Type`/`Data` 指回出错行。

取舍：维持"发布不验证、运行期可选验证"。把唯一性检查强加进 `AppendCore` 会改变默认配置语义
（现有依赖重复键发布的用户会破裂），且验证需要全表扫描，不应由发布路径强制承担。
引用完整性同理：`IValidatable<T>.Validate` 经 `ReferenceSet.Exists`
（`src/MasterMemory/Validation/ReferenceSet.cs:30`）在运行期报告
`Exists failed: QuestPath.RewardItemId -> PathItem.ItemId, value = 999, PK(QuestId) = 20`，
见 `QueryPathTest.ReferenceFailure_ReportsStableMessageAndLocation`。

### 2.3 RangeView 是否复制数据

不复制。`RangeView<T>` 是只读结构体视图：持有数组引用 + `left`/`right`/`ascendant`
（`RangeView.cs:47`），索引器按边界换算（:28），`Reverse` 只翻转方向标志（:21）。
唯一的复制发生在更早的环节：二级索引数组本身是表构造时 `CloneAndSortBy` 的克隆
（`TableBase.cs:41`），主键视图（`All`）则直接包住原始 `data` 数组。

验证：`QueryPathTest.RangeView_DoesNotCopyData` 用反射断言
`All` 的 `orderedData` 与 `GetRawDataUnsafe()` 同一引用、`FindByLevel(2)` 的 `orderedData`
与表的 `secondaryIndex1` 字段同一引用且不同于 `data`，元素层面也是同一引用。

取舍：维持零拷贝视图。边界与空值语义由 `RangeViewTest` 与
`QueryPathTest.RangeQueries_RespectSliceBoundaries` 覆盖：越界索引抛
`ArgumentOutOfRangeException`、空视图 `Count == 0`、`min > max` 归一为空视图而非交换边界
（`TableBase.cs:172`）。

### 2.4 其他不变量

- 字符串驻留：`internString: true`（默认）时重复字符串反序列化为同一实例
  （`InternStringResolver.cs:35`）；`false` 时各行独立实例。
  验证：`QueryPathTest.StringInterning_IsControlledByConstructorOption`。
- 空表：追加空集合或根本不追加（header 缺失）都得到 `Count == 0` 的表，
  `FindBy` 抛 `KeyNotFoundException`（`TableBase.cs:55`）、`TryFindBy` 返回 false、
  范围查询返回空视图。验证：`QueryPathTest.EmptyTable_AndMissingTable_BehaveAsEmpty`。
- 比较器：字符串键默认 `StringComparer.Ordinal`（生成期烧入，`GenerationContext.cs:102`），
  排序区分大小写且按码位（'Z' 排在 'a' 前）。
  验证：`QueryPathTest.UniqueSecondaryKey_UsesOrdinalStringComparer`。
- 重复键相对顺序：`Array.Sort` 不稳定，非唯一索引中相等键的相对顺序不属于契约。
  本数据在 net9.0 上观测到 `FindByLevel(2) = [30,40,50]`、`SortByLevel = [10,30,40,50,20]`
  （5 元素走插入排序，恰好稳定），但测试只断言集合与有序性，不断言该顺序。

## 3. 生成 API 与返回顺序记录（QuestPath 固定数据）

数据（追加顺序即此顺序，主键乱序）：

| QuestId | Name | Level | Category | RewardItemId |
| --- | --- | --- | --- | --- |
| 30 | gamma | 2 | common | 100 |
| 10 | alpha | 1 | common | 100 |
| 50 | Zulu | 2 | rare | 200 |
| 20 | beta | 3 | common | 300 |
| 40 | delta | 2 | rare | 300 |

生成 API（`MasterMemory.Tests.Tables.QuestPathTable`，反射锁定于
`GeneratedApiSurface_MatchesKeyShapes`）：

- `FindByQuestId(int) -> QuestPath`、`TryFindByQuestId(int, out QuestPath) -> bool`
- `FindClosestByQuestId(int, bool) -> QuestPath`、`FindRangeByQuestId(int, int, bool) -> RangeView<QuestPath>`
- `FindByName(string) -> QuestPath`（唯一）、`FindByLevel(int) -> RangeView<QuestPath>`（非唯一）
- `SortByName` / `SortByLevel -> RangeView<QuestPath>`；`DatabaseBuilder.Append(IEnumerable<QuestPath>)`

返回顺序（确定性部分）：

- `All` = `GetRawDataUnsafe()` = 10,20,30,40,50（发布期主键排序）；`AllReverse` = 50,40,30,20,10
- `SortByName` = Zulu,alpha,beta,delta,gamma（ordinal：'Z' < 'a'）
- `FindRangeByQuestId(20,40)` = 20,30,40；`ascendant: false` = 40,30,20；边界向内吸附：`(15,35)` = 20,30
- `FindByLevel(2)` = 集合 {30,40,50}（重复键相对顺序非契约，见 2.4）

## 4. 错误分支与兼容性说明

本次改动不新增库代码错误分支，也不改动默认配置语义：所有失败路径都复用现有接口可区分的通道——
键缺失 → `KeyNotFoundException`（`TableBase.cs:55`）；视图越界 → `ArgumentOutOfRangeException`
（`RangeView.cs:28`）；数据约束 → `ValidateResult.FailedResults`（不抛异常，
`ValidateResult.cs`）。新增内容仅为最小 schema、测试与本文档。

## 5. 验证流程

```
dotnet restore MasterMemory.sln
dotnet test MasterMemory.sln --no-restore
```

测试全部基于固定数据与顺序加载（默认 `maxDegreeOfParallelism = 1`），无随机源、无后台任务、
无临时产物；生成器输出位于各项目 `obj/`（已被 gitignore），测试退出后不遗留状态。
