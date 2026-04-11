// UI控制逻辑
let currentTaskId = null;
let currentView = 'author'; // 'author' | 'single-post'

// 页面加载完成
document.addEventListener('DOMContentLoaded', () => {
    initializeApp();
});

// 初始化应用
function initializeApp() {
    // 作者下载页按钮绑定
    document.getElementById('selectPathBtn').addEventListener('click', () => selectSavePath('savePath'));
    // 单作品下载页按钮绑定
    document.getElementById('selectPathBtnSingle').addEventListener('click', () => selectSavePath('savePathSingle'));

    document.getElementById('startBtn').addEventListener('click', startTask);
    document.getElementById('stopBtn').addEventListener('click', stopTask);
    document.getElementById('resumeBtn').addEventListener('click', resumeTask);
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

    const isAuthor = view === 'author';
    document.getElementById('viewAuthor').style.display = isAuthor ? '' : 'none';
    document.getElementById('viewSinglePost').style.display = isAuthor ? 'none' : '';
    document.getElementById('tabAuthor').classList.toggle('active', isAuthor);
    document.getElementById('tabSinglePost').classList.toggle('active', !isAuthor);

    addLog(`已切换到${isAuthor ? '作者' : '单作品'}下载模式`, 'info');
}

// 选择保存路径（inputId 决定写入哪个输入框）
async function selectSavePath(inputId) {
    const path = await window.electronAPI.selectFolder();
    if (path) {
        document.getElementById(inputId).value = path;
        addLog(`已选择保存路径: ${path}`, 'info');
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

    if (currentView === 'author') {
        const service = document.getElementById('service').value;
        const username = document.getElementById('username').value.trim();
        const savePath = document.getElementById('savePath').value.trim();
        const limit = parseInt(document.getElementById('limit').value) || 50;
        const concurrent = parseInt(document.getElementById('concurrent').value) || 5;
        const skipExisting = document.getElementById('skipExisting').checked;

        if (!service) { addLog('请选择服务平台', 'error'); return; }
        if (!username) { addLog('请输入作者 ID', 'error'); return; }
        if (!savePath) { addLog('请选择保存路径', 'error'); return; }

        config = { mode: 'author', service, username, savePath, limit, concurrent, skipExisting };
        taskTitle = `${service} - ${username}`;
        taskInfo = `限制数量: ${limit} | 并发数: ${concurrent}`;
    } else {
        const postUrl = document.getElementById('postUrl').value.trim();
        const savePath = document.getElementById('savePathSingle').value.trim();
        const concurrent = parseInt(document.getElementById('concurrentSingle').value) || 5;
        const skipExisting = document.getElementById('skipExistingSingle').checked;

        if (!postUrl) { addLog('请输入作品链接', 'error'); return; }
        if (!savePath) { addLog('请选择保存路径', 'error'); return; }

        let parsedPost;
        try {
            parsedPost = window.scraper.parsePostUrl(postUrl);
        } catch (error) {
            addLog(error.message, 'error');
            return;
        }

        config = { mode: 'single-post', postUrl, savePath, concurrent, skipExisting };
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
function stopTask() {
    window.scraper.stop();
    document.getElementById('startBtn').disabled = false;
    document.getElementById('stopBtn').disabled = true;
    updateTaskStatus('stopped');
    addLog('任务已停止', 'warning');
}

// 恢复任务
async function resumeTask() {
    const progressFile = await window.electronAPI.selectProgressFile();
    if (!progressFile) return;

    addLog(`选择进度文件: ${progressFile}`, 'info');

    // 读取进度文件，根据 config.mode 切换到对应视图
    try {
        const raw = await window.electronAPI.fs.read(progressFile);
        const progressData = JSON.parse(raw);
        const mode = progressData?.config?.mode;
        if (mode === 'single-post') {
            switchView('single-post');
        } else {
            switchView('author');
        }

        // 生成任务标题
        let taskTitle = '恢复任务';
        let taskInfo = '续传中...';
        if (mode === 'single-post' && progressData.config?.postUrl) {
            try {
                const p = window.scraper.parsePostUrl(progressData.config.postUrl);
                taskTitle = `${p.service} - post/${p.postId}`;
                taskInfo = `单作品下载 | 续传`;
            } catch (_) {}
        } else if (progressData.config?.service && progressData.config?.username) {
            taskTitle = `${progressData.config.service} - ${progressData.config.username}`;
            taskInfo = `续传 | 限制数量: ${progressData.config.limit || '-'}`;
        }

        createTaskDisplay(taskTitle, taskInfo);
    } catch (e) {
        addLog(`读取进度文件失败: ${e.message}`, 'error');
    }

    document.getElementById('startBtn').disabled = true;
    document.getElementById('stopBtn').disabled = false;

    await window.scraper.resumeFromProgress(progressFile);

    document.getElementById('startBtn').disabled = false;
    document.getElementById('stopBtn').disabled = true;
    updateTaskStatus('completed');
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
    logEntry.innerHTML = `<span class="log-time">[${time}]</span> ${message}`;

    logsContainer.appendChild(logEntry);
    logsContainer.scrollTop = logsContainer.scrollHeight;

    const logs = logsContainer.querySelectorAll('.log-entry');
    if (logs.length > 200) {
        logs[0].remove();
    }
}

// 清空日志
function clearLogs() {
    const logsContainer = document.getElementById('systemLogs');
    logsContainer.innerHTML = '<div class="empty-state">日志已清空</div>';
    addLog('日志已清空', 'info');
}
