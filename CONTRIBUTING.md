# 参与贡献 / Contributing

## 中文

欢迎为 ScreenTime 报告问题、改进说明、修复错误或开发功能。项目采用 [MIT 许可证](LICENSE)，允许修改和分发，须保留许可证要求的版权及许可声明。

1. 在 GitHub 点击 **Fork**，将公开仓库复制到自己的账号。
2. 克隆自己的 Fork，在其中创建功能分支，例如 `fix/widget-position` 或 `docs/english-guide`。
3. 完成修改；代码修改请在 Windows 上构建并运行与改动相关的检查。只改文档时检查内容与链接即可。
4. 将分支推送到自己的 Fork，向 `lsaac335/windows-screen-time-open-source` 的 **main** 提交 Pull Request。
5. 在 PR 中说明解决的问题、改后的行为、验证方法和已知限制，由维护者审核后决定是否合并。

提交 PR 不需要成为本仓库的协作者，也不需要向维护者申请直接写入权限。新建本仓库分支本身不会授予其他人写入权限；长期协作权限由维护者另行决定。

构建和逻辑检查：

```powershell
.\build.ps1
dotnet run --project tests/CoreTests/CoreTests.csproj -c Release
```

构建输出为 `首版`。构建环境和功能口径见 [README](README.md)。请保留前台、活跃、可见和亮屏指标的区别；涉及计时或存储修改时，说明对旧记录的影响。请使用合成数据复现问题，提交前移除真实 `data`、设置、日志、账户凭据及构建产物。

## English

Contributions to ScreenTime are welcome: bug reports, documentation, fixes, and features. The [MIT License](LICENSE) permits modification and redistribution, subject to retaining its required copyright and permission notices.

1. Click **Fork** on GitHub to create a copy under your account.
2. Clone your fork and create a branch, such as `fix/widget-position` or `docs/english-guide`.
3. Make your changes. For code changes, build on Windows and run checks relevant to the change. For documentation-only edits, review the content and links.
4. Push the branch to your fork and open a pull request targeting **main** in `lsaac335/windows-screen-time-open-source`.
5. Explain the problem, resulting behavior, validation, and known limits. A maintainer will review the proposal and decide whether to merge it.

You do not need collaborator status or direct write access to propose a pull request. Creating a branch in the upstream repository does not grant others write access; ongoing collaborator access is a separate maintainer decision.

Use the build/check commands above; build output goes to `首版`. See the [English overview](README.en.md) for prerequisites and metric definitions. Preserve the distinction between foreground, active, visible, and screen-on time. Explain compatibility with existing records when changing tracking or storage. Reproduce issues with synthetic data, and exclude real usage records, settings, logs, credentials, and build artifacts from submissions.

For the general workflow, see [GitHub's contribution guide](https://docs.github.com/en/get-started/exploring-projects-on-github/contributing-to-a-project).
