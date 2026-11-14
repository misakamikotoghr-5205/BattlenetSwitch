# 战网一键切号

适用于 Windows 10/11 x64 的本地 Battle.net 桌面切号工具。保存账号及区服的登录快照后，双击列表中的账号，即可退出战网、恢复目标登录状态并重启客户端。

## 使用方法

1. 在战网客户端登录你的账号，勾选「保持登录」，然后点击工具里的「保存当前快照」。
2. 点击「登录新账号」，选区服，在战网完成登录并保持登录，再保存快照。
3. 双击列表中的账号切换。如果战网要求重新登录或完成验证，请在战网客户端操作。
4. 如果工具找不到战网，点击「设置路径」，选择电脑上的 `Battle.net.exe` 或 `Battle.net Launcher.exe`。

工具使用当前 Windows 用户已保存的账号设置和战网加密凭据。快照存放在 `%LOCALAPPDATA%\BattleNetQuickSwitch`。请勿上传或分享该目录。源码和发行文件中没有账号快照。工具本身没有网络请求或账号同步功能。

切换和失败回退已用模拟客户端验证，真实战网登录尚未完成 Windows 实机验证。该项目为独立第三方工具，与暴雪娱乐或网易无隶属关系。

## 编译

安装 .NET 8 SDK，在仓库根目录运行：

```powershell
dotnet publish src/Desktop/BattleNetQuickSwitch.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded -o dist
```

生成的 `dist/BattleNetQuickSwitch.exe` 自带运行时，可以在 Windows 上双击启动。EXE 未做数字签名。
