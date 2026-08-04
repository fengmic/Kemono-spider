// UI控制逻辑
let currentTaskId = null;
let currentView = 'author'; // 'author' | 'single-post' | 'settings' | 'repair'

// 跨平台路径拼接
function joinPath(...parts) {
    return parts.join('/');
}

// 全局设置存储
const defaultSettings = {
    retries: 5,
    imageTimeout: 60,
    videoTimeout: 1200,
    pageRequestDelay: 1500,
    defaultConcurrent: 5,
    batchDelay: 500,
    skipExistingDefault: true,
    proxyType: '',
    proxyHost: '',
    proxyPort: 1080,
    proxyUsername: '',
    proxyPassword: ''
};

let globalSettings = { ...defaultSettings };

// 修复相关变量
let scanResults = {
    path: '',
    files: [],
    totalSize: 0,
    corruptedCount: 0
};

// 页面加载完成
document.addEventListener('DOMContentLoaded', () => {
    initializeApp();
});

// 初始化应用
function initializeApp() {
    loadGlobalSettings();
    applyProxySettings();

    // 作者下载页按钮绑定
    document.getElementById('selectPathBtn').addEventListener('click', () => selectSavePath('savePath'));
    // 单作品下载页按钮绑定
    document.getElementById('selectPathBtnSingle').addEventListener('click', () => selectSavePath('savePathSingle'));

    // 设置页面按钮绑定
    document.getElementById('saveSettingsBtn').addEventListener('click', saveGlobalSettings);
    document.getElementById('resetSettingsBtn').addEventListener('click', resetGlobalSettings);

    // 修复页面按钮绑定
    document.getElementById('selectRepairPathBtn').addEventListener('click', () => selectRepairPath());
    document.getElementById('scanBtn').addEventListener('click', scanCorruptedFiles);
    document.getElementById('repairBtn').addEventListener('click', repairCorruptedFiles);

    // 续传页面按钮绑定
    document.getElementById('selectResumePathBtn').addEventListener('click', selectResumePath);

    document.getElementById('cancelIncrementalBtn').addEventListener('click', cancelIncrementalMode);
    document.getElementById('startBtn').addEventListener('click', startTask);
    document.getElementById('stopBtn').addEventListener('click', stopTask);
    document.getElementById('clearLogsBtn').addEventListener('click', clearLogs);

    addLog('应用已启动', 'success');

    // 显示系统信息
    const platformName = window.electronAPI.platform === 'win32' ? 'Windows' :
                         window.electronAPI.platform === 'darwin' ? 'macOS' :
                         window.electronAPI.platform === 'linux' ? 'Linux' :
                         window.electronAPI.platform;
    const archName = window.electronAPI.arch === 'x64' ? '64位' :
                     window.electronAPI.arch === 'ia32' ? '32位' :
                     window.electronAPI.arch === 'arm64' ? 'ARM 64位' :
                     window.electronAPI.arch;

    addLog(`系统: ${platformName} ${archName}`, 'info');
    addLog(`Electron: ${window.electronAPI.versions.electron} | Node: ${window.electronAPI.versions.node}`, 'info');
}

// 切换视图
function switchView(view) {
    if (currentView === view) return;
    currentView = view;

    const viewIds = ['author', 'single-post', 'resume', 'settings', 'repair'];
    const tabIds = ['tabAuthor', 'tabSinglePost', 'tabResume', 'tabSettings', 'tabRepair'];
    const displayNames = {
        'author': '作者集合',
        'single-post': '单作品',
        'resume': '续传',
        'settings': '设置',
        'repair': '修复'
    };

    // 隐藏所有视图
    viewIds.forEach(id => {
        const el = document.getElementById(`view${id.charAt(0).toUpperCase() + id.slice(1).replace(/-./g, x => x[1].toUpperCase())}`);
        if (el) el.style.display = 'none';
    });

    // 移除所有标签的激活状态
    tabIds.forEach(id => {
        const el = document.getElementById(id);
        if (el) el.classList.remove('active');
    });

    // 显示选中视图及标签
    const viewIdMap = {
        'author': 'viewAuthor',
        'single-post': 'viewSinglePost',
        'resume': 'viewResume',
        'settings': 'viewSettings',
        'repair': 'viewRepair'
    };
    const tabIdMap = {
        'author': 'tabAuthor',
        'single-post': 'tabSinglePost',
        'resume': 'tabResume',
        'settings': 'tabSettings',
        'repair': 'tabRepair'
    };

    const viewEl = document.getElementById(viewIdMap[view]);
    const tabEl = document.getElementById(tabIdMap[view]);
    if (viewEl) viewEl.style.display = '';
    if (tabEl) tabEl.classList.add('active');

    // 控制下载按钮组的显示：作者、单作品、续传页面显示，其他隐藏
    const buttonGroup = document.getElementById('downloadButtonGroup');
    if (buttonGroup) {
        buttonGroup.style.display = (view === 'author' || view === 'single-post' || view === 'resume') ? '' : 'none';
    }

    if (view !== 'settings' && view !== 'repair') {
        addLog(`已切换到${displayNames[view] || view}下载模式`, 'info');
    } else {
        addLog(`已切换到${displayNames[view] || view}模式`, 'info');
    }
}

// 选择保存路径（inputId 决定写入哪个输入框）
async function selectSavePath(inputId) {
    const path = await window.electronAPI.selectFolder();
    if (path) {
        document.getElementById(inputId).value = path;
        addLog(`已选择保存路径: ${path}`, 'info');
    }
}

// 选择修复路径
async function selectRepairPath() {
    const path = await window.electronAPI.selectFolder();
    if (path) {
        document.getElementById('repairPath').value = path;
        document.getElementById('repairBtn').disabled = true;
        document.getElementById('repairStatus').innerHTML = '<span>已选择路径，请先点击"扫描损坏文件"按钮</span>';
        addLog(`已选择修复路径: ${path}`, 'info');
    }
}

// 选择续传路径
async function selectResumePath() {
    const path = await window.electronAPI.selectFolder();
    if (path) {
        document.getElementById('resumeProgressPath').value = path;
        await loadResumeInfo(path);
        addLog(`已选择续传路径: ${path}`, 'info');
    }
}

// 加载续传信息
async function loadResumeInfo(basePath) {
    try {
        const progressFile = joinPath(basePath, 'download_progress.json');
        const progressData = await window.electronAPI.fs.read(progressFile);
        const progressJson = JSON.parse(progressData);
        const config = progressJson?.config;

        if (!config) {
            throw new Error('缺少配置信息');
        }

        // 显示任务信息
        let taskInfo = '';
        if (config.mode === 'author') {
            taskInfo = `
                <div><strong>模式:</strong> 作者集合下载</div>
                <div><strong>平台:</strong> ${config.service}</div>
                <div><strong>作者ID:</strong> ${config.username}</div>
                <div><strong>并发数:</strong> ${config.concurrent}</div>
                ${config.limit > 0 ? `<div><strong>数量限制:</strong> ${config.limit}</div>` : ''}
                <div><strong>跳过已存在:</strong> ${config.skipExisting ? '是' : '否'}</div>
            `;
        } else if (config.mode === 'single-post') {
            taskInfo = `
                <div><strong>模式:</strong> 单作品下载</div>
                <div><strong>链接:</strong> ${config.postUrl}</div>
                <div><strong>并发数:</strong> ${config.concurrent}</div>
                <div><strong>跳过已存在:</strong> ${config.skipExisting ? '是' : '否'}</div>
            `;
        }

        // 统计已下载的作品数
        let completedCount = 0;
        for (const key in progressJson) {
            if (key !== 'config' && progressJson[key].completed_time) {
                completedCount++;
            }
        }

        taskInfo += `<div style="margin-top: 10px; color: var(--accent-green);"><strong>已完成作品:</strong> ${completedCount} 个</div>`;

        const detailsEl = document.getElementById('resumeDetails');
        if (detailsEl) {
            detailsEl.innerHTML = taskInfo;
        }

        const infoEl = document.getElementById('resumeInfo');
        if (infoEl) {
            infoEl.style.display = 'block';
        }

        const statusEl = document.getElementById('resumeStatus');
        if (statusEl) {
            statusEl.innerHTML = '<span style="color: var(--accent-green);">✓ 进度文件加载成功，点击"开始下载"按钮继续下载</span>';
        }

        // 存储续传配置供开始下载时使用
        window.resumeConfig = {
            path: basePath,
            config: config,
            progressJson: progressJson
        };

        addLog('续传信息加载成功', 'success');
    } catch (error) {
        const statusEl = document.getElementById('resumeStatus');
        if (statusEl) {
            statusEl.innerHTML = `<span style="color: var(--accent-red);">✗ 加载失败: ${error.message}</span>`;
        }
        addLog(`加载续传信息失败: ${error.message}`, 'error');
    }
}

// 加载全局设置
function loadGlobalSettings() {
    const saved = localStorage.getItem('kemonoSettings');
    if (saved) {
        try {
            globalSettings = { ...defaultSettings, ...JSON.parse(saved) };
        } catch (e) {
            globalSettings = { ...defaultSettings };
        }
    }
    updateSettingsUI();
    if (window.scraper) {
        window.scraper.applyGlobalSettings(globalSettings);
    }
}

// 更新UI显示设置
function updateSettingsUI() {
    document.getElementById('globalRetries').value = globalSettings.retries;
    document.getElementById('imageTimeout').value = globalSettings.imageTimeout;
    document.getElementById('videoTimeout').value = globalSettings.videoTimeout;
    document.getElementById('pageRequestDelay').value = globalSettings.pageRequestDelay;
    document.getElementById('defaultConcurrent').value = globalSettings.defaultConcurrent;
    document.getElementById('batchDelay').value = globalSettings.batchDelay;
    document.getElementById('skipExistingDefault').checked = globalSettings.skipExistingDefault;
    document.getElementById('proxyType').value = globalSettings.proxyType || '';
    document.getElementById('proxyHost').value = globalSettings.proxyHost || '';
    document.getElementById('proxyPort').value = globalSettings.proxyPort || 1080;
    document.getElementById('proxyUsername').value = globalSettings.proxyUsername || '';
    document.getElementById('proxyPassword').value = globalSettings.proxyPassword || '';

    const authorConcurrent = document.getElementById('concurrent');
    const singleConcurrent = document.getElementById('concurrentSingle');
    const skipExisting = document.getElementById('skipExisting');
    const skipExistingSingle = document.getElementById('skipExistingSingle');
    if (authorConcurrent) authorConcurrent.value = globalSettings.defaultConcurrent;
    if (singleConcurrent) singleConcurrent.value = globalSettings.defaultConcurrent;
    if (skipExisting) skipExisting.checked = globalSettings.skipExistingDefault;
    if (skipExistingSingle) skipExistingSingle.checked = globalSettings.skipExistingDefault;
}

// 保存全局设置
function saveGlobalSettings() {
    globalSettings = {
        retries: parseInt(document.getElementById('globalRetries').value) || 5,
        imageTimeout: parseInt(document.getElementById('imageTimeout').value) || 60,
        videoTimeout: parseInt(document.getElementById('videoTimeout').value) || 1200,
        pageRequestDelay: parseInt(document.getElementById('pageRequestDelay').value) || 1500,
        defaultConcurrent: parseInt(document.getElementById('defaultConcurrent').value) || 5,
        batchDelay: parseInt(document.getElementById('batchDelay').value) || 500,
        skipExistingDefault: document.getElementById('skipExistingDefault').checked,
        proxyType: document.getElementById('proxyType').value,
        proxyHost: document.getElementById('proxyHost').value.trim(),
        proxyPort: parseInt(document.getElementById('proxyPort').value) || 1080,
        proxyUsername: document.getElementById('proxyUsername').value.trim(),
        proxyPassword: document.getElementById('proxyPassword').value
    };

    localStorage.setItem('kemonoSettings', JSON.stringify(globalSettings));

    // 将设置传递给爬虫
    window.scraper.applyGlobalSettings(globalSettings);

    // 应用代理设置
    applyProxySettings();

    const statusEl = document.getElementById('settingsStatus');
    statusEl.innerHTML = '<span style="color: var(--accent-green);">✓ 设置已保存</span>';
    setTimeout(() => {
        statusEl.innerHTML = '';
    }, 2000);

    addLog('全局设置已保存', 'success');
}

// 恢复默认设置
function resetGlobalSettings() {
    if (confirm('确定要恢复默认设置吗？')) {
        globalSettings = { ...defaultSettings };
        localStorage.setItem('kemonoSettings', JSON.stringify(globalSettings));
        updateSettingsUI();
        
        window.scraper.applyGlobalSettings(globalSettings);

        const statusEl = document.getElementById('settingsStatus');
        statusEl.innerHTML = '<span style="color: var(--accent-green);">✓ 已恢复默认设置</span>';
        setTimeout(() => {
            statusEl.innerHTML = '';
        }, 2000);

        addLog('设置已恢复为默认值', 'info');
    }
}

// 应用代理设置
async function applyProxySettings() {
    const proxyType = globalSettings.proxyType;
    if (!proxyType) {
        try {
            await window.electronAPI.setProxy({ type: '' });
            addLog('代理已禁用，使用直连', 'info');
        } catch (e) {
            addLog(`禁用代理失败: ${e.message}`, 'error');
        }
        return;
    }

    const proxyHost = globalSettings.proxyHost;
    const proxyPort = globalSettings.proxyPort;
    if (!proxyHost || !proxyPort) {
        addLog('代理主机或端口为空，跳过代理设置', 'warning');
        return;
    }

    try {
        await window.electronAPI.setProxy({
            type: proxyType,
            host: proxyHost,
            port: proxyPort,
            username: globalSettings.proxyUsername || '',
            password: globalSettings.proxyPassword || ''
        });
        addLog(`代理已设置: ${proxyType}://${proxyHost}:${proxyPort}`, 'success');
    } catch (e) {
        addLog(`设置代理失败: ${e.message}`, 'error');
    }
}

// 扫描损坏的文件
async function scanCorruptedFiles() {
    const repairPath = document.getElementById('repairPath').value.trim();
    if (!repairPath) {
        addLog('请先选择修复路径', 'error');
        return;
    }

    const fileSizeLimit = parseInt(document.getElementById('fileSizeLimit').value) || 100;
    const statusEl = document.getElementById('repairStatus');
    statusEl.innerHTML = '<span style="color: var(--accent-orange);">正在扫描...</span>';

    addLog(`开始扫描损坏文件，大小限制: ${fileSizeLimit}KB`, 'info');

    try {
        scanResults = await window.scraper.scanCorruptedFiles(repairPath, fileSizeLimit);
        
        if (scanResults.files.length === 0) {
            statusEl.innerHTML = '<span style="color: var(--accent-green);">✓ 未发现损坏文件</span>';
            document.getElementById('repairBtn').disabled = true;
            addLog('扫描完成：没有发现损坏文件', 'success');
        } else {
            const html = `
                <div>
                    <strong>扫描结果：</strong><br>
                    总文件数: ${scanResults.totalCount}<br>
                    正常文件: ${scanResults.totalCount - scanResults.files.length}<br>
                    <span style="color: var(--accent-red);">损坏文件: ${scanResults.files.length}</span><br>
                    <span style="color: var(--accent-red);">总大小: ${(scanResults.totalSize / 1024).toFixed(2)}KB</span><br>
                    <br>
                    <strong>前10个损坏文件:</strong><br>
                    ${scanResults.files.slice(0, 10).map(f => `• ${f.name} (${(f.size / 1024).toFixed(2)}KB)`).join('<br>')}
                    ${scanResults.files.length > 10 ? `<br>... 还有 ${scanResults.files.length - 10} 个文件` : ''}
                </div>
            `;
            statusEl.innerHTML = html;
            document.getElementById('repairBtn').disabled = false;
            addLog(`扫描完成：发现 ${scanResults.files.length} 个损坏文件`, 'warning');
        }
    } catch (error) {
        statusEl.innerHTML = `<span style="color: var(--accent-red);">✗ 扫描失败: ${error.message}</span>`;
        addLog(`扫描失败: ${error.message}`, 'error');
    }
}

// 修复损坏的文件
async function repairCorruptedFiles() {
    if (scanResults.files.length === 0) {
        addLog('没有损坏文件需要修复', 'warning');
        return;
    }

    if (!confirm(`确定要删除并重新下载 ${scanResults.files.length} 个损坏文件吗？`)) {
        return;
    }

    const repairConcurrent = parseInt(document.getElementById('repairConcurrent').value) || 5;
    const statusEl = document.getElementById('repairStatus');
    statusEl.innerHTML = '<span style="color: var(--accent-orange);">正在修复...</span>';

    document.getElementById('repairBtn').disabled = true;
    document.getElementById('scanBtn').disabled = true;

    try {
        // 调用修复函数，传入路径信息
        const repairPath = document.getElementById('repairPath').value.trim();
        const progressFile = joinPath(repairPath, 'download_progress.json');
        
        await window.scraper.repairCorruptedFiles(repairPath, repairConcurrent, progressFile);
        statusEl.innerHTML = '<span style="color: var(--accent-green);">✓ 修复完成</span>';
        addLog('损坏文件修复完成', 'success');
        
        // 清空扫描结果
        scanResults = { path: '', files: [], totalSize: 0, corruptedCount: 0 };
    } catch (error) {
        statusEl.innerHTML = `<span style="color: var(--accent-red);">✗ 修复失败: ${error.message}</span>`;
        addLog(`修复失败: ${error.message}`, 'error');
    } finally {
        document.getElementById('repairBtn').disabled = true;
        document.getElementById('scanBtn').disabled = false;
    }
}

// HTML 转义
function escapeHtml(value) {
    const div = document.createElement('div');
    div.textContent = value ?? '';
    return div.innerHTML;
}

// 开始任务
async function startTask() {
    let config;
    let taskTitle;
    let taskInfo;

    // 续传模式
    if (currentView === 'resume') {
        if (!window.resumeConfig) {
            addLog('请先选择续传路径', 'error');
            return;
        }

        config = { ...window.resumeConfig.config };
        const resetProgress = document.getElementById('resetProgressCheckbox').checked;
        if (resetProgress) {
            config.forceFresh = true;
        }
        const progressJson = window.resumeConfig.progressJson;
        
        // 计算已完成和待完成的作品数
        let completedCount = 0;
        let totalCount = 0;
        for (const key in progressJson) {
            if (key !== 'config') {
                totalCount++;
                if (progressJson[key].completed_time) {
                    completedCount++;
                }
            }
        }

        taskTitle = config.mode === 'author' ? `${config.service} - ${config.username}` : `post/${config.postUrl.split('/').pop()}`;
        taskInfo = `续传模式 | 已完成: ${completedCount}/${totalCount}`;

        addLog(`继续下载任务，已完成 ${completedCount}/${totalCount} 个作品`, 'info');
        
        document.getElementById('startBtn').disabled = true;
        document.getElementById('stopBtn').disabled = false;

        createTaskDisplay(taskTitle, taskInfo);

        await window.scraper.startScraping(config);

        document.getElementById('startBtn').disabled = false;
        document.getElementById('stopBtn').disabled = true;

        updateTaskStatus('completed');
        return;
    }

    // 原有的下载模式
    if (currentView === 'author') {
        const service = document.getElementById('service').value;
        const username = document.getElementById('username').value.trim();
        const savePath = document.getElementById('savePath').value.trim();
        const limit = parseInt(document.getElementById('limit').value) || 0;
        const concurrent = parseInt(document.getElementById('concurrent').value) || 5;
        const skipExisting = document.getElementById('skipExisting').checked;
        const useThumbnail = document.getElementById('useThumbnail').checked;

        if (!service) { addLog('请选择服务平台', 'error'); return; }
        if (!username) { addLog('请输入作者 ID', 'error'); return; }
        if (!savePath) { addLog('请选择保存路径', 'error'); return; }

        config = { mode: 'author', service, username, savePath, limit, concurrent, skipExisting, useThumbnail, batchDelay: globalSettings.batchDelay };
        taskTitle = `${service} - ${username}`;
        taskInfo = `限制数量: ${limit || '不限制'} | 并发数: ${concurrent}`;
    } else {
        const postUrl = document.getElementById('postUrl').value.trim();
        const savePath = document.getElementById('savePathSingle').value.trim();
        const concurrent = parseInt(document.getElementById('concurrentSingle').value) || 5;
        const skipExisting = document.getElementById('skipExistingSingle').checked;
        const useThumbnail = document.getElementById('useThumbnailSingle').checked;

        if (!postUrl) { addLog('请输入作品链接', 'error'); return; }
        if (!savePath) { addLog('请选择保存路径', 'error'); return; }

        let parsedPost;
        try {
            parsedPost = window.scraper.parsePostUrl(postUrl);
        } catch (error) {
            addLog(error.message, 'error');
            return;
        }

        config = { mode: 'single-post', postUrl, savePath, concurrent, skipExisting, useThumbnail, batchDelay: globalSettings.batchDelay };
        taskTitle = `${parsedPost.service} - post/${parsedPost.postId}`;
        taskInfo = `单作品下载 | 并发数: ${concurrent}`;
    }

    document.getElementById('startBtn').disabled = true;
    document.getElementById('stopBtn').disabled = false;

    createTaskDisplay(taskTitle, taskInfo);

    await window.scraper.startScraping(config);

    document.getElementById('startBtn').disabled = false;
    document.getElementById('stopBtn').disabled = true;

    updateTaskStatus('completed');
}

// 停止任务
async function stopTask() {
    await window.scraper.stop();
    document.getElementById('startBtn').disabled = false;
    document.getElementById('stopBtn').disabled = true;
    updateTaskStatus('stopped');
    addLog('任务已停止', 'warning');
}

// 创建任务显示
function createTaskDisplay(title, infoText) {
    const tasksContainer = document.getElementById('activeTasks');
    tasksContainer.innerHTML = '';

    const taskId = 'task-' + Date.now();
    currentTaskId = taskId;

    const taskHtml = `
        <div class="task-item" id="${taskId}">
            <div class="task-header">
                <div class="task-title">${escapeHtml(title)}</div>
                <div class="task-status status-running">运行中</div>
            </div>
            <div class="task-info">
                ${escapeHtml(infoText)} | 已下载: <span id="${taskId}-downloaded">0</span> 文件
            </div>
            <div class="progress-bar">
                <div class="progress-fill" id="${taskId}-progress" style="width: 0%"></div>
            </div>
            <div class="progress-text">
                <span id="${taskId}-current">准备中...</span>
                <span id="${taskId}-percent">0%</span>
            </div>
        </div>
    `;

    tasksContainer.innerHTML = taskHtml;
    document.getElementById('activeCount').textContent = '1';
}

// 增量下载状态更新
window.updateIncrementalStatus = function(stats) {
    if (!stats || !stats.incremental) return;

    const banner = document.getElementById('incrementalBanner');
    const detail = document.getElementById('incrementalBannerDetail');
    if (!banner || !detail) return;

    detail.textContent = `检测到该作者已有 ${stats.completedCount} 个作品下载完成，将仅下载新增作品`;
    banner.style.display = '';

    // 存储增量信息以便取消
    window._incrementalInfo = stats;

    addLog(`增量下载模式：已跳过 ${stats.completedCount} 个已完成作品`, 'success');
};

// 取消增量模式
function cancelIncrementalMode() {
    const banner = document.getElementById('incrementalBanner');
    if (banner) banner.style.display = 'none';
    window._incrementalInfo = null;

    // 停止当前任务后以全新模式重启
    window.scraper.stop().then(() => {
        addLog('增量模式已取消，请重新点击"开始下载"以全新模式下载', 'info');
    });
}

// 更新任务进度
window.updateTaskProgress = function(downloaded, total, currentFile) {
    if (!currentTaskId) return;

    const percent = total > 0 ? Math.round((downloaded / total) * 100) : 0;

    const progressBar = document.getElementById(`${currentTaskId}-progress`);
    const downloadedSpan = document.getElementById(`${currentTaskId}-downloaded`);
    const currentSpan = document.getElementById(`${currentTaskId}-current`);
    const percentSpan = document.getElementById(`${currentTaskId}-percent`);

    if (progressBar) progressBar.style.width = `${percent}%`;
    if (downloadedSpan) downloadedSpan.textContent = downloaded;
    if (currentSpan) currentSpan.textContent = currentFile || '处理中...';
    if (percentSpan) percentSpan.textContent = `${percent}%`;
}

// 更新任务状态
function updateTaskStatus(status) {
    if (!currentTaskId) return;

    const taskItem = document.getElementById(currentTaskId);
    if (!taskItem) return;

    const statusElement = taskItem.querySelector('.task-status');
    if (!statusElement) return;

    statusElement.classList.remove('status-running', 'status-stopped', 'status-completed');

    switch (status) {
        case 'running':
            statusElement.classList.add('status-running');
            statusElement.textContent = '运行中';
            break;
        case 'stopped':
            statusElement.classList.add('status-stopped');
            statusElement.textContent = '已停止';
            break;
        case 'completed':
            statusElement.classList.add('status-completed');
            statusElement.textContent = '已完成';
            break;
    }
}

// 添加日志
window.addLog = function(message, type = 'info') {
    const logsContainer = document.getElementById('systemLogs');
    const time = new Date().toLocaleTimeString('zh-CN', { hour12: false });

    const logEntry = document.createElement('div');
    logEntry.className = `log-entry log-${type}`;
    logEntry.innerHTML = `<span class="log-time">[${time}]</span> ${escapeHtml(message)}`;

    logsContainer.appendChild(logEntry);
    logsContainer.scrollTop = logsContainer.scrollHeight;

    const MAX_LOG_ENTRIES = 500;
    const logs = logsContainer.querySelectorAll('.log-entry');
    if (logs.length > MAX_LOG_ENTRIES) {
        logs[0].remove();
    }
}

// 清空日志
function clearLogs() {
    const logsContainer = document.getElementById('systemLogs');
    logsContainer.innerHTML = '<div class="empty-state">日志已清空</div>';
    addLog('日志已清空', 'info');
}
