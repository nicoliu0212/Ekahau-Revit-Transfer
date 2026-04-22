# 发布操作指南 — Publishing to GitHub

完整步骤把这个项目推到 https://github.com/nicoliu0212/Ekahau-Revit-Transfer 并发布 v2.4.0 release。

---

## 已经准备好的文件

```
✓ .gitignore                              忽略 bin/obj/.wix/Output/.user/...
✓ LICENSE                                 MIT (Copyright 2026 Nico Liu)
✓ README.md                               主页 — 带徽章、目录、架构说明、安装步骤
✓ CHANGELOG.md                            v1.0 → v2.4.0 完整版本历史
✓ 使用说明.md                             完整中文用户手册
✓ RELEASE_NOTES_v2.4.0.md                 首版发布说明（贴到 GitHub Release 页面）
✓ .github/workflows/release.yml           CI: push tag v* → 自动 build + 创建 release + 上传 MSI
✓ .github/ISSUE_TEMPLATE/bug_report.md    Bug 模板
✓ .github/ISSUE_TEMPLATE/feature_request.md
✓ .github/ISSUE_TEMPLATE/config.yml       禁用空白 issue + 链到中文文档
✓ .github/PULL_REQUEST_TEMPLATE.md
✓ Installer/EkahauWiFiTools-v2.4.0.msi    最新构建产物
```

Git 已初始化，第一个 commit 已建好（45 个文件）：
```
9eda04f Initial public release — v2.4.0
```

---

## 步骤 1 — 推到 GitHub

### 在 GitHub 上准备 repo

1. 打开 https://github.com/nicoliu0212/Ekahau-Revit-Transfer
2. **Settings → 顶部 Default branch** 确认是 `main`
3. **Settings → Danger Zone → Change visibility → Make public**（什么时候做都行，但发 release 之前必须公开，不然别人看不到）

> 如果 repo 还不存在：
> - 在 GitHub 创建一个 **空** repo (不要勾 README / .gitignore / license — 我们已经有了)
> - 名字 = `Ekahau-Revit-Transfer`
> - 选 Private 也行，先开发再公开

### 推送代码

在 `D:\Claude\EkahauRevitPlugin\` 里跑：

```powershell
git remote add origin https://github.com/nicoliu0212/Ekahau-Revit-Transfer.git
git push -u origin main
```

第一次推送会让你登录（建议用 [GitHub CLI](https://cli.github.com/) 或 Personal Access Token）。

---

## 步骤 2 — 第一次 Release（v2.4.0）

有两种方式，选一种。

### 方式 A — 用 git tag 自动 release（推荐）

我配置好的 GitHub Actions workflow 会监听 `v*` 标签：tag 一推上去，CI 自动 build 三个 runtime + 打 MSI + 创建 release + 上传 MSI 文件。

```powershell
git tag -a v2.4.0 -m "Release v2.4.0 — first public release"
git push origin v2.4.0
```

然后到 https://github.com/nicoliu0212/Ekahau-Revit-Transfer/actions 看 build。
约 3-5 分钟后，https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases 上会自动出现 v2.4.0，附带 `EkahauWiFiTools-v2.4.0.msi`。

**第一次 build 可能因为 .NET 10 SDK 还在 RC 阶段失败**。如果 GitHub Actions 找不到 .NET 10：
- 编辑 `.github/workflows/release.yml`
- 把 `dotnet-quality: preview` 改成 `dotnet-quality: rc`，或者
- 临时把 net10 target 移除（修改 `.csproj`，把 `TargetFrameworks` 改成只有 `net48;net8.0-windows`）

### 方式 B — 手动上传 MSI

```powershell
# 1. 在 GitHub 网页上：Releases → "Draft a new release"
# 2. Tag: v2.4.0   (Create new tag on publish)
# 3. Title: v2.4.0
# 4. Description: 把 RELEASE_NOTES_v2.4.0.md 的内容粘贴进去
# 5. Drag & drop:  D:\Claude\EkahauRevitPlugin\Installer\EkahauWiFiTools-v2.4.0.msi
# 6. Publish release
```

---

## 步骤 3 — 后续小版本更新流程

每次想发新版本：

```powershell
# 1. 修代码 + 在 CHANGELOG.md 顶部加新版本号
# 2. 改 Installer/Package.wxs 里的 Version=
# 3. 提交
git add -A
git commit -m "feat: <描述>"
git push

# 4. 打 tag — CI 自动 build + release + 上传 MSI
git tag -a v2.4.1 -m "Release v2.4.1"
git push origin v2.4.1
```

---

## 步骤 4 — 让项目看起来更专业（可选）

### 加 Topics
GitHub repo 主页右上角齿轮 → 加几个 topic：
```
revit, ekahau, wifi, wifi-planning, autodesk, revit-addin, dotnet, csharp, wpf, wifi-survey
```

### 加 About 描述
主页右上角齿轮 → Description:
> Bi-directional bridge between Autodesk Revit and Ekahau AI Pro for WiFi planning. Supports Revit 2023-2027.

→ Website: 留空或填你 GitHub Pages

### 截图（README 里目前没有）
真正高质量的项目都有截图/GIF。建议拍这几张：
1. **Ribbon 截图** — 显示 WiFi Tools tab 上的 5 个按钮
2. **Param Config 类型映射对话框**
3. **ESX Export 视图选择 + 映射检查**
4. **ESX Read 中放置 AP 标记后的视图**
5. **AP Place 完成后的 WiFi Plan view + AP Schedule 截图**

把图存到 `docs/images/` 里，README 顶部插入：
```markdown
![Ribbon](docs/images/ribbon.png)
```

或做个动图（用 [ScreenToGif](https://www.screentogif.com/)）效果更好。

### Pin 到 profile
在你的 GitHub profile 上 pin 这个 repo（右上角头像 → Pinned repositories）。

---

## 步骤 5 — 持续维护

| 时机 | 操作 |
|------|------|
| 收到 issue | 用模板回复，定期标 label (bug / enhancement / question) |
| 收到 PR | 走 PR 模板的 checklist 验证 |
| 每个 release | 更新 CHANGELOG.md，bump `Installer/Package.wxs` 里的 `Version=`，打 tag |
| Revit 新版发布 | 加新的 target framework + SDK 引用，更新 install.ps1 / WiX |

---

## 当前 git 状态

```
D:\Claude\EkahauRevitPlugin\
├── .git/                                 ← 已初始化
├── .github/                              ← CI + 模板
├── .gitignore
├── EkahauRevitPlugin/                    ← 源代码
├── Installer/                            ← WiX MSI 定义
├── 使用说明.md
├── CHANGELOG.md
├── LICENSE
├── README.md
├── RELEASE_NOTES_v2.4.0.md
├── PUBLISH_GUIDE.md                      ← 这个文件
├── fix-revit-2027.bat
└── install.ps1

Branch: main
Commits: 1 (Initial public release — v2.4.0)
Tracked: 45 files
```

---

## 检查清单 — 推送前确认

- [ ] LICENSE 里的 `Copyright (c) 2026 Nico Liu` — 名字对吗？
- [ ] README.md 里所有 `nicoliu0212/Ekahau-Revit-Transfer` 链接对吗？
- [ ] CHANGELOG.md 底部的 release tag URL 用的也是这个 repo 路径？
- [ ] `.github/ISSUE_TEMPLATE/config.yml` 里的链接对吗？
- [ ] `.github/workflows/release.yml` 里 `${{ github.repository }}` 会被 GitHub 自动替换为 `nicoliu0212/Ekahau-Revit-Transfer` — 不用手动改

如果想改名字 / 链接 / repo 路径，全局搜索替换 `nicoliu0212/Ekahau-Revit-Transfer` 即可。

---

完了！发布顺利的话，几分钟内就能在 https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases/tag/v2.4.0 看到下载链接。
