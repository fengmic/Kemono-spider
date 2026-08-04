const { contextBridge, ipcRenderer } = require('electron');

// 暴露安全的API给渲染进程
contextBridge.exposeInMainWorld('electronAPI', {
    // 选择文件夹
    selectFolder: () => ipcRenderer.invoke('select-folder'),
    
    // 选择进度文件
    selectProgressFile: () => ipcRenderer.invoke('select-progress-file'),
    
    // HTTP请求
    httpRequest: (options) => ipcRenderer.invoke('http-request', options),
    
    // 文件系统操作
    fs: {
        mkdir: (dirPath) => ipcRenderer.invoke('fs-mkdir', dirPath),
        exists: (filePath) => ipcRenderer.invoke('fs-exists', filePath),
        stat: (filePath) => ipcRenderer.invoke('fs-stat', filePath),
        write: (filePath, content) => ipcRenderer.invoke('fs-write', filePath, content),
        read: (filePath) => ipcRenderer.invoke('fs-read', filePath),
        download: (url, filePath, timeout = 0) => ipcRenderer.invoke('fs-download', url, filePath, timeout),
        scanDir: (dirPath) => ipcRenderer.invoke('fs-scan-dir', dirPath),
        deleteFile: (filePath) => ipcRenderer.invoke('fs-delete-file', filePath)
    },
    
    // 代理设置
    setProxy: (config) => ipcRenderer.invoke('set-proxy', config),

    // 平台信息
    platform: process.platform,
    arch: process.arch, // 系统架构: x64, ia32, arm64等
    
    // 版本信息
    versions: {
        node: process.versions.node,
        chrome: process.versions.chrome,
        electron: process.versions.electron
    }
});
