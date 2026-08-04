const { app, BrowserWindow, ipcMain, dialog, screen, net } = require('electron');
const path = require('path');
const https = require('https');
const http = require('http');
const fs = require('fs');
const fsp = fs.promises;
const zlib = require('zlib');

let mainWindow;

// 窗口状态文件路径
const windowStateFile = path.join(app.getPath('userData'), 'window-state.json');

// 加载窗口状态
function loadWindowState() {
    try {
        if (fs.existsSync(windowStateFile)) {
            const data = fs.readFileSync(windowStateFile, 'utf8');
            return JSON.parse(data);
        }
    } catch (error) {
        console.error('Failed to load window state:', error);
    }
    return null;
}

// 保存窗口状态
function saveWindowState() {
    try {
        if (!mainWindow) return;
        
        const bounds = mainWindow.getBounds();
        const state = {
            x: bounds.x,
            y: bounds.y,
            width: bounds.width,
            height: bounds.height,
            isMaximized: mainWindow.isMaximized()
        };
        
        fs.writeFileSync(windowStateFile, JSON.stringify(state, null, 2));
    } catch (error) {
        console.error('Failed to save window state:', error);
    }
}

// 创建主窗口
function createWindow() {
    // 获取主显示器信息
    const primaryDisplay = screen.getPrimaryDisplay();
    const { width: screenWidth, height: screenHeight } = primaryDisplay.workAreaSize;
    
    // 加载上次的窗口状态
    const windowState = loadWindowState();
    
    // 默认窗口配置
    let windowConfig = {
        width: 1200,
        height: 800,
        minWidth: 900,
        minHeight: 600,
        webPreferences: {
            nodeIntegration: false,
            contextIsolation: true,
            preload: path.join(__dirname, 'preload.js')
        },
        autoHideMenuBar: true,
        backgroundColor: '#0a0a0f',
        show: false,
        icon: path.join(__dirname, 'assets/icon.ico')
    };
    
    // 如果有保存的状态，使用保存的位置和尺寸
    if (windowState) {
        windowConfig.x = windowState.x;
        windowConfig.y = windowState.y;
        windowConfig.width = windowState.width;
        windowConfig.height = windowState.height;
    } else {
        // 默认居中显示
        windowConfig.x = Math.floor((screenWidth - windowConfig.width) / 2);
        windowConfig.y = Math.floor((screenHeight - windowConfig.height) / 2);
    }
    
    mainWindow = new BrowserWindow(windowConfig);

    // 加载本地HTML文件
    mainWindow.loadFile('index.html');
    
    // 窗口准备好后显示
    mainWindow.once('ready-to-show', () => {
        // 如果之前是最大化状态，恢复最大化
        const windowState = loadWindowState();
        if (windowState && windowState.isMaximized) {
            mainWindow.maximize();
        }
        mainWindow.show();
    });

    // 打开开发者工具（开发时使用）
    // mainWindow.webContents.openDevTools();
    
    // 监听窗口移动和调整大小事件
    let saveTimeout;
    const debouncedSave = () => {
        clearTimeout(saveTimeout);
        saveTimeout = setTimeout(() => {
            saveWindowState();
        }, 500);
    };
    
    mainWindow.on('resize', debouncedSave);
    mainWindow.on('move', debouncedSave);
    mainWindow.on('maximize', saveWindowState);
    mainWindow.on('unmaximize', saveWindowState);

    mainWindow.on('closed', () => {
        mainWindow = null;
    });
}

// 应用准备就绪
app.whenReady().then(() => {
    createWindow();

    app.on('activate', () => {
        if (BrowserWindow.getAllWindows().length === 0) {
            createWindow();
        }
    });
});

// 所有窗口关闭
app.on('window-all-closed', () => {
    // 保存窗口状态
    saveWindowState();
    
    if (process.platform !== 'darwin') {
        app.quit();
    }
});

// 应用退出前保存窗口状态
app.on('before-quit', () => {
    saveWindowState();
});

// IPC通信处理 - 设置代理
ipcMain.handle('set-proxy', async (event, proxyConfig) => {
    const { type, host, port, username, password } = proxyConfig || {};

    if (!type) {
        // 清除代理设置，恢复直连
        try {
            await mainWindow.webContents.session.setProxy({
                mode: 'direct'
            });
            mainWindow.webContents.session.closeAllConnections();
            return { success: true, message: '代理已禁用，使用直连' };
        } catch (error) {
            return { success: false, message: error.message };
        }
    }

    try {
        // 构造 Chromium 的 proxyRules 格式
        // 格式: "scheme=host:port", 多个规则用分号分隔
        let proxyRules;
        const authPrefix = username ? `${encodeURIComponent(username)}:${encodeURIComponent(password)}@` : '';
        const proxyAddr = `${authPrefix}${host}:${port}`;

        switch (type.toLowerCase()) {
            case 'http':
                proxyRules = `http=${proxyAddr}`;
                break;
            case 'https':
                proxyRules = `https=${proxyAddr}`;
                break;
            case 'socks5':
                proxyRules = `socks5=${proxyAddr}`;
                break;
            default:
                proxyRules = `${type}=${proxyAddr}`;
        }

        await mainWindow.webContents.session.setProxy({
            proxyRules,
            proxyBypassRules: '<local>'
        });
        mainWindow.webContents.session.closeAllConnections();
        return { success: true, message: `代理已设置: ${type}://${host}:${port}` };
    } catch (error) {
        return { success: false, message: error.message };
    }
});

// IPC通信处理 - 选择文件夹
ipcMain.handle('select-folder', async () => {
    const result = await dialog.showOpenDialog(mainWindow, {
        properties: ['openDirectory']
    });
    
    if (!result.canceled && result.filePaths.length > 0) {
        return result.filePaths[0];
    }
    return null;
});

// IPC通信处理 - 选择进度文件
ipcMain.handle('select-progress-file', async () => {
    const result = await dialog.showOpenDialog(mainWindow, {
        properties: ['openFile'],
        filters: [
            { name: 'JSON文件', extensions: ['json'] },
            { name: '所有文件', extensions: ['*'] }
        ]
    });
    
    if (!result.canceled && result.filePaths.length > 0) {
        return result.filePaths[0];
    }
    return null;
});

// IPC通信处理 - HTTP请求
ipcMain.handle('http-request', async (event, options) => {
    return new Promise((resolve, reject) => {
        const url = new URL(options.url);
        const protocol = url.protocol === 'https:' ? https : http;
        
        const reqOptions = {
            hostname: url.hostname,
            port: url.port,
            path: url.pathname + url.search,
            method: options.method || 'GET',
            headers: options.headers || {},
            timeout: options.timeout || 30000
        };

        const req = protocol.request(reqOptions, (res) => {
            // 处理压缩响应（gzip/deflate）
            let stream = res;
            const encoding = res.headers['content-encoding'];
            
            if (encoding === 'gzip') {
                stream = res.pipe(zlib.createGunzip());
            } else if (encoding === 'deflate') {
                stream = res.pipe(zlib.createInflate());
            }
            
            let data = '';
            stream.setEncoding('utf8');
            
            stream.on('data', (chunk) => {
                data += chunk;
            });
            
            stream.on('end', () => {
                resolve({
                    statusCode: res.statusCode,
                    headers: res.headers,
                    data: data
                });
            });
            
            stream.on('error', (error) => {
                reject(error);
            });
        });

        req.on('error', (error) => {
            reject(error);
        });

        req.on('timeout', () => {
            req.destroy();
            reject(new Error('Request timeout'));
        });

        if (options.body) {
            req.write(options.body);
        }

        req.end();
    });
});

// IPC通信处理 - 文件系统操作
ipcMain.handle('fs-mkdir', async (event, dirPath) => {
    await fsp.mkdir(dirPath, { recursive: true });
    return true;
});

ipcMain.handle('fs-exists', async (event, filePath) => {
    return fs.existsSync(filePath);
});

ipcMain.handle('fs-stat', async (event, filePath) => {
    const stats = await fsp.stat(filePath);
    return {
        size: stats.size,
        isFile: stats.isFile(),
        isDirectory: stats.isDirectory()
    };
});

ipcMain.handle('fs-write', async (event, filePath, content) => {
    await fsp.writeFile(filePath, content);
    return true;
});

ipcMain.handle('fs-read', async (event, filePath) => {
    return await fsp.readFile(filePath, 'utf8');
});

ipcMain.handle('fs-download', async (event, url, filePath, timeout = 0) => {
    return new Promise((resolve, reject) => {
        let timeoutHandle = null;
        let requestCompleted = false;
        let file = null;
        const tempPath = `${filePath}.part`;

        const cleanupAndReject = (error) => {
            if (requestCompleted) return;
            requestCompleted = true;
            if (timeoutHandle) clearTimeout(timeoutHandle);
            if (file) {
                file.destroy();
            }
            try {
                if (fs.existsSync(tempPath)) {
                    fs.unlinkSync(tempPath);
                }
            } catch (e) {}
            reject(error);
        };

        const request = net.request({
            method: 'GET',
            url: url,
            redirect: 'follow',
        });

        request.setHeader('User-Agent', 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36');
        request.setHeader('Accept', 'image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8');
        request.setHeader('Accept-Language', 'zh-CN,zh;q=0.9,en;q=0.8');
        request.setHeader('Referer', 'https://kemono.cr/');

        request.on('response', (response) => {
            if (response.statusCode >= 400) {
                cleanupAndReject(new Error(`HTTP ${response.statusCode}`));
                return;
            }

            try {
                if (fs.existsSync(tempPath)) {
                    fs.unlinkSync(tempPath);
                }
            } catch (error) {
                cleanupAndReject(error);
                return;
            }

            file = fs.createWriteStream(tempPath);
            let downloadedBytes = 0;
            const expectedBytes = Number.parseInt(response.headers['content-length'] || '0', 10);

            if (timeout > 0) {
                timeoutHandle = setTimeout(() => {
                    if (requestCompleted) return;
                    request.abort();
                    cleanupAndReject(new Error(`下载超时: 超过 ${timeout}ms`));
                }, timeout);
            }

            response.on('data', (chunk) => {
                downloadedBytes += chunk.length;
            });

            response.pipe(file);

            file.on('finish', () => {
                if (requestCompleted) return;
                if (timeoutHandle) clearTimeout(timeoutHandle);
                file.close(async (error) => {
                    if (error) {
                        cleanupAndReject(error);
                        return;
                    }

                    if (expectedBytes > 0 && downloadedBytes !== expectedBytes) {
                        cleanupAndReject(new Error(`下载不完整: ${downloadedBytes}/${expectedBytes} bytes`));
                        return;
                    }

                    try {
                        await fsp.rename(tempPath, filePath);
                    } catch (renameError) {
                        try {
                            await fsp.copyFile(tempPath, filePath);
                            await fsp.unlink(tempPath);
                        } catch (copyError) {
                            cleanupAndReject(copyError);
                            return;
                        }
                    }

                    requestCompleted = true;
                    resolve(true);
                });
            });

            file.on('error', (err) => {
                cleanupAndReject(err);
            });

            response.on('error', (err) => {
                cleanupAndReject(err);
            });
        });

        request.on('error', (err) => {
            cleanupAndReject(err);
        });

        request.on('abort', () => {
            cleanupAndReject(new Error('请求被中止'));
        });

        request.end();
    });
});

ipcMain.handle('fs-scan-dir', async (event, dirPath) => {
    const fileList = [];

    async function scanDir(dir) {
        try {
            const entries = await fsp.readdir(dir, { withFileTypes: true });
            for (const entry of entries) {
                const fullPath = path.join(dir, entry.name);
                try {
                    if (entry.isDirectory()) {
                        await scanDir(fullPath);
                    } else if (entry.isFile()) {
                        try {
                            const stats = await fsp.stat(fullPath);
                            fileList.push({
                                name: entry.name,
                                path: fullPath,
                                size: stats.size
                            });
                        } catch (statErr) {
                            console.warn(`无法读取文件信息: ${fullPath} - ${statErr.code}`);
                        }
                    }
                } catch (entryErr) {
                    console.warn(`处理条目出错: ${fullPath} - ${entryErr.code}`);
                }
            }
        } catch (error) {
            console.error(`扫描目录出错: ${error.message}`);
        }
    }

    await scanDir(dirPath);
    return fileList;
});

ipcMain.handle('fs-delete-file', async (event, filePath) => {
    await fsp.unlink(filePath);
    return true;
});


