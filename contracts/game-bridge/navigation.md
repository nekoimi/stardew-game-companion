# 导航目标与回执

`navigate-to` 和 `inspect-route` 共用 `locationId`、可选 `navigationTarget`、`tile` 与 `shopId`。默认 `exact` 必须提供精确站立 tile，`interact` 必须提供已观察对象的 tile，程序从相邻可通行格选择操作站位和朝向；`shop` 必须提供 shopId，程序从原生柜台元数据解析地图和站位，不打开商店、不采购，也不绕过营业和事件检查。无效字段组合在 TypeScript 工具边界和原生传输边界分别拒绝。

动作是单次立即执行的行程，入口与路径仅在该动作内保存，不创建持久化队列。入口图先生成候选行程，只精算当前地图，进入后重查下一段；预览仍完成全路线验证，其远图碰撞证据有限，不能保证未来原生解锁与动态障碍。原生玩家通过实际输入踩踏出口、通过门动作进入建筑，实际地图变化确认过渡；不能把触发区当精确站立格，也不能用强制坐标写入或 warpFarmer 代替导航。

有界路段缓存复用前核对所有格与连接碰撞，对象/地形变化失效对应地图，建筑、地图资源、读档及新日重建或清理缓存；不缓存不可达结果。执行中实时检查碰撞，短等后重算当前路段，必要时在本次行程内排除失败入口并最多两次尝试其他入口。失败、取消和接管终止旧导航并释放执行权。

回执 `details.navigation` 提供 navigationTarget、planningMs（搜索实际 CPU 墙钟累计，不含行走）、cacheHits、replans、alternateEntrances、exploredEntrances、pathSearches 和当前 searchActivity。部分行程的 visitedLocations/hopCount 是已经确认的实际地图，不能把候选入口表当作已走过。桌面执行阶段新增 walking-route、crossing-map、recovering-route，planning-route 保留。
