# 0.1.9 回滚选幕栏布局

日期：2026-10-11。需求依据：DESIGN v1.13。

原因：旧栏固定在地图顶部 y=100，与原生藏品列表占用同一区域。修复为底部锚定的居中背景栏，容器按内容排列；栏外空白不接收点击。仅改变展示布局，不改变回滚状态或存档格式，接受已有 0.1.8 配对记录。

## 验证

- Mac beta v0.111.0：真实原生窗口运行既有 RollbackProbe，通过跨幕预览、取消、节点点击等检查。真实视口截图中“第 2 幕／第 3 幕／取消”位于底部，顶部藏品区无选幕控件。
- Computer Use：使用副环境存档，继续游戏后打开回滚，观察启动窗口及系统“放大窗口”两种大小。底部栏均完整可见，且与绘图按钮、分享按钮分离；点击取消成功返回奖励界面。该手动场景仅含一幕目标，跨幕按钮点击由前项原生检查覆盖。
- 四个目标均构建成功，0 警告、0 错误。Windows 与 Mac 正式版本轮未重新运行；本轮未执行完整对局、多模组或联机测试，此前结果只作为历史记录。
- 测试退出后副环境原配置与模组已恢复；未用主存档测试。

| 目标 | DLL SHA-256 | 本轮加载／界面 |
| --- | --- | --- |
| macos-public-beta | `54c49382d10d62ca235b0d475e388b492c0a9e454b27d516ee7e46b04fd16f3e` | 通过 |
| macos-public | `43450ee9ce47fa2a1b3d3fba1c3b7d7195b5c309d473fcd979ad3102233e17a8` | 未执行 |
| windows-public-beta | `0ac291db102759fef7838dd1347652034078e8b12443b5857150159dafb56aff` | 未执行 |
| windows-public | `53deba27dc9423db5b0314633179cd7b7a611a856cbd2a460f52c11f27dd11be` | 未执行 |

本地证据：`artifacts/macos/public-beta/lab-20261011T061119860742Z/run-record.json`、`artifacts/0.1.9/layout-public-beta-20261011-141119/profile-after/nosuffering-rollback.png`，以及 `artifacts/0.1.9/ui-20261011-141242/restore.json` 和本轮 Computer Use 截图。安装结果由发行附件 `deployment.json` 单独记录，安装后主游戏人工复测不记为通过。
