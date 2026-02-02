// UI控制逻辑
let currentTaskId = null;

// 页面加载完成
document.addEventListener('DOMContentLoaded', () => {
    initializeApp();
});

// 初始化应用
function initializeApp() {
    // 绑定事件
    document.getElementById('selectPathBtn').addEventListener('click', selectSavePath);
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

// 选择保存路径
async function selectSavePath() {
    const path = await window.electronAPI.selectFolder();
    if (path) {
        document.getElementById('savePath').value = path;
        addLog(`已选择保存路径: ${path}`, 'info');
    }
}

// 开始任务
async function startTask() {
    const service = document.getElementById('service').value;
    const username = document.getElementById('username').value.trim();
    const savePath = document.getElementById('savePath').value.trim();
    const limit = parseInt(document.getElementById('limit').value) || 50;
    const concurrent = parseInt(document.getElementById('concurrent').value) || 5;
    const skipExisting = document.getElementById('skipExisting').checked;

    // 验证输入
    if (!service) {
        addLog('请选择服务平台', 'error');
        return;
    }

    if (!username) {
        addLog('请输入作者ID', 'error');
        return;
    }

    if (!savePath) {
        addLog('请选择保存路径', 'error');
        return;
    }

    // 禁用开始按钮，启用停止按钮
    document.getElementById('startBtn').disabled = true;
    document.getElementById('stopBtn').disabled = false;

    // 创建任务显示
    createTaskDisplay(service, username, limit);

    // 配置任务
    const config = {
        service,
        username,
        savePath,
        limit,
        concurrent,
        skipExisting
    };

    // 开始爬取
    await window.scraper.startScraping(config);

    // 任务完成，恢复按钮状态
    document.getElementById('startBtn').disabled = false;
    document.getElementById('stopBtn').disabled = true;

    // 更新任务状态
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
    if (progressFile) {
        addLog(`选择进度文件: ${progressFile}`, 'info');
        
        // 禁用开始按钮，启用停止按钮
        document.getElementById('startBtn').disabled = true;
        document.getElementById('stopBtn').disabled = false;

        // 恢复任务
        await window.scraper.resumeFromProgress(progressFile);

        // 任务完成，恢复按钮状态
        document.getElementById('startBtn').disabled = false;
        document.getElementById('stopBtn').disabled = true;
    }
}

// 创建任务显示
function createTaskDisplay(service, username, limit) {
    const tasksContainer = document.getElementById('activeTasks');
    
    // 清空之前的任务
    tasksContainer.innerHTML = '';

    const taskId = 'task-' + Date.now();
    currentTaskId = taskId;

    const taskHtml = `
        <div class="task-item" id="${taskId}">
            <div class="task-header">
                <div class="task-title">${service} - ${username}</div>
                <div class="task-status status-running">运行中</div>
            </div>
            <div class="task-info">
                限制数量: ${limit} | 已下载: <span id="${taskId}-downloaded">0</span> 文件
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

    // 移除所有状态类
    statusElement.classList.remove('status-running', 'status-stopped', 'status-completed');

    // 添加新状态
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
    
    // 自动滚动到底部
    logsContainer.scrollTop = logsContainer.scrollHeight;

    // 限制日志数量
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
