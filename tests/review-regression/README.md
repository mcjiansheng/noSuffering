# 原生审查回归

`ReviewRegression` 是独立测试模组，引用真实游戏和待测 NoSuffering DLL。测试需要已准备的 `.tools/game-lab/<platform>-<branch>/prepared.json` 及该副环境的 `default` 用户目录。

先按项目构建说明生成 NoSuffering，将待测安装目录复制到 `artifacts/review-regression/<phase>/<platform>-<branch>/NoSuffering`。旧版复现使用 `baseline`，修复验证使用 `fixed`。再编译测试项目，例如 Mac beta：

```sh
.tools/dotnet/dotnet build tests/review-regression/ReviewRegression.csproj -c Release \
  -p:Sts2AssemblyDir="$PWD/.tools/game-lab/macos-public-beta/game/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64" \
  -p:NoSufferingDll="$PWD/artifacts/review-regression/fixed/macos-public-beta/NoSuffering/NoSuffering.dll" \
  -p:Sts2Branch=public-beta \
  -p:BaseOutputPath="$PWD/tests/review-regression/bin/public-beta/" \
  -p:BaseIntermediateOutputPath="$PWD/tests/review-regression/obj/public-beta/" \
  -p:MSBuildProjectExtensionsPath="$PWD/tests/review-regression/obj/public-beta/"

python3 tests/review-regression/run.py --platform macos --branch public-beta --phase fixed --case storage
python3 tests/review-regression/run.py --platform macos --branch public-beta --phase fixed --case config-ui
python3 tests/review-regression/run.py --platform macos --branch public-beta --phase fixed --case network --timeout 360
```

Windows 使用 `.tools/dotnet/dotnet.exe`、实际 Windows 程序集路径和 `--platform windows`；正式版传 `Sts2Branch=public` 并使用对应的 `public` 输出目录与 runner 分支。

Runner 为每次执行建立全新的游戏副本和 `NoSufferingLab/review/...` 用户目录，只安装产品与测试模组；会话标记精确绑定可执行文件和用户目录。网络使用两个原生进程及本机 ENet，测试中只重启房主，客户端保留进程。网络用例之间应顺序执行以复用原生测试端口。每次运行输出 `run-record.json`、各进程报告及原生日志。

`baseline` 的通过表示故障复现；`fixed` 的通过表示修复条件成立。测试通过真实原生 API 安排场景，证据类型和各检查的具体范围见 `docs/testing/2026-10-11-review-regressions.md`。
