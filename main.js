const { app, BrowserWindow, ipcMain, dialog, screen } = require('electron');
const path = require('path');
const https = require('https');
const http = require('http');
const fs = require('fs');
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
            timeout: options.timeout || 30000,
            rejectUnauthorized: false // 忽略SSL证书验证
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
    return new Promise((resolve, reject) => {
        fs.mkdir(dirPath, { recursive: true }, (err) => {
            if (err) reject(err);
            else resolve(true);
        });
    });
});

ipcMain.handle('fs-exists', async (event, filePath) => {
    return fs.existsSync(filePath);
});

ipcMain.handle('fs-write', async (event, filePath, content) => {
    return new Promise((resolve, reject) => {
        fs.writeFile(filePath, content, (err) => {
            if (err) reject(err);
            else resolve(true);
        });
    });
});

ipcMain.handle('fs-read', async (event, filePath) => {
    return new Promise((resolve, reject) => {
        fs.readFile(filePath, 'utf8', (err, data) => {
            if (err) reject(err);
            else resolve(data);
        });
    });
});

ipcMain.handle('fs-download', async (event, url, filePath) => {
    const doRequest = (requestUrl) => new Promise((resolve, reject) => {
        const urlObj = new URL(requestUrl);
        const protocol = urlObj.protocol === 'https:' ? https : http;

        const request = protocol.get({
            hostname: urlObj.hostname,
            path: urlObj.pathname + urlObj.search,
            headers: {
                'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36'
            },
            rejectUnauthorized: false
        }, (response) => {
            if (response.statusCode === 301 || response.statusCode === 302 || response.statusCode === 307 || response.statusCode === 308) {
                const location = response.headers['location'];
                if (!location) {
                    reject(new Error(`重定向但缺少 Location 头: ${requestUrl}`));
                    return;
                }
                const redirectUrl = new URL(location, requestUrl).toString();
                resolve(doRequest(redirectUrl));
                return;
            }

            const file = fs.createWriteStream(filePath);
            response.pipe(file);

            file.on('finish', () => {
                file.close();
                resolve(true);
            });

            file.on('error', (err) => {
                fs.unlink(filePath, () => {});
                reject(err);
            });
        });

        request.on('error', (err) => {
            fs.unlink(filePath, () => {});
            reject(err);
        });
    });

    return doRequest(url);
});

