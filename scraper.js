// Kemono爬虫核心逻辑 - 完全按照kemono_gui copy.py重构
class KemonoScraper {
    constructor() {
        this.baseUrl = 'https://kemono.cr';
        this.isRunning = false;
        this.shouldStop = false;
        this.currentTask = null;
        this.sslContext = true; // JavaScript中使用Node.js的https模块，默认验证SSL

        // HTTP请求头（对应Python版本的headers）
        this.headers = {
            'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36',
            'Accept': 'text/css,*/*;q=0.1',
            'Accept-Language': 'en-US,en;q=0.5',
            'Accept-Encoding': 'gzip, deflate',
            'Connection': 'keep-alive',
            'Upgrade-Insecure-Requests': '1'
        };
    }

    // 日志输出
    log(message, type = 'info') {
        if (window.addLog) {
            window.addLog(message, type);
        }
        console.log(`[${type}] ${message}`);
    }

    // 更新进度
    updateProgress(downloaded, total, currentFile = '') {
        if (window.updateTaskProgress) {
            window.updateTaskProgress(downloaded, total, currentFile);
        }
    }

    // 延迟函数
    sleep(ms) {
        return new Promise(resolve => setTimeout(resolve, ms));
    }

    // 创建请求对象（对应Python的_create_request）
    _createRequest(url, referer = null) {
        const requestHeaders = { ...this.headers };
        if (referer) {
            requestHeaders['Referer'] = referer;
        }
        return requestHeaders;
    }

    // 发起HTTP请求
    async _makeRequest(url, referer = null, retries = 5) {
        for (let attempt = 1; attempt <= retries; attempt++) {
            try {
                const headers = this._createRequest(url, referer);
                const response = await window.electronAPI.httpRequest({
                    url: url,
                    method: 'GET',
                    headers: headers,
                    timeout: 30000
                });

                if (response.statusCode === 200) {
                    return { statusCode: 200, data: response.data };
                } else {
                    return { statusCode: response.statusCode, data: null };
                }
            } catch (error) {
                if (attempt < retries) {
                    this.log(`请求失败 (${attempt}/${retries}): ${error.message}，正在重试...`, 'warning');
                } else {
                    this.log(`请求失败，已达最大重试次数: ${error.message}`, 'error');
                    throw error;
                }
            }
        }
    }

    // 访问用户主页以设置必要的cookies和referer（对应Python的visit_homepage）
    async visitHomepage(userId, service = 'fanbox', offset = 0) {
        let userUrl;
        if (offset === 0) {
            userUrl = `${this.baseUrl}/${service}/user/${userId}`;
        } else {
            userUrl = `${this.baseUrl}/${service}/user/${userId}?o=${offset}`;
        }

        this.log(`正在访问用户主页: ${userUrl}`);

        try {
            const response = await this._makeRequest(userUrl);
            this.log(`主页访问状态码: ${response.statusCode}`);
            return response.statusCode === 200;
        } catch (error) {
            this.log(`访问主页时出现错误: ${error.message}`, 'error');
            return false;
        }
    }

    // 解析Kemono单作品链接
    parsePostUrl(input) {
        try {
            const url = new URL(input);
            const baseHost = new URL(this.baseUrl).hostname;
            const allowedHosts = new Set([baseHost, `www.${baseHost}`]);

            if (!allowedHosts.has(url.hostname)) {
                throw new Error('请输入有效的 Kemono 作品链接');
            }

            const pathname = url.pathname.replace(/\/+$/, '');
            const match = pathname.match(/^\/([^/]+)\/user\/([^/]+)\/post\/([^/]+)$/);

            if (!match) {
                throw new Error('作品链接格式无效，需为 /<service>/user/<userId>/post/<postId>');
            }

            const [, service, userId, postId] = match;
            return {
                service,
                userId,
                postId,
                postUrl: `${url.origin}/${service}/user/${userId}/post/${postId}`
            };
        } catch (error) {
            if (error instanceof TypeError) {
                throw new Error('请输入有效的 Kemono 作品链接');
            }
            throw error;
        }
    }

    // 获取用户资料信息（对应Python的get_user_profile）
    async getUserProfile(userId, service = 'fanbox') {
        // 先访问用户主页以绕过反爬机制
        if (!await this.visitHomepage(userId, service)) {
            this.log('无法访问用户主页，终止操作', 'error');
            return null;
        }

        // 等待一小段时间，避免请求过快
        await this.sleep(1000);

        // 访问API获取用户资料
        const profileUrl = `${this.baseUrl}/api/v1/${service}/user/${userId}/profile`;
        const userUrl = `${this.baseUrl}/${service}/user/${userId}`; // 用于设置referer
        this.log(`正在获取用户资料: ${profileUrl}`);

        try {
            const response = await this._makeRequest(profileUrl, userUrl);
            this.log(`用户资料访问状态码: ${response.statusCode}`);

            if (response.statusCode === 200) {
                const profile = JSON.parse(response.data);
                return profile;
            } else {
                this.log(`用户资料请求失败，状态码: ${response.statusCode}`, 'error');
                return null;
            }
        } catch (error) {
            this.log(`请求用户资料时出现错误: ${error.message}`, 'error');
            return null;
        }
    }

    // 获取用户的所有作品列表（支持分页）（对应Python的get_user_posts）
    async getUserPosts(userId, service = 'fanbox', limit = null) {
        const allPosts = [];
        let offset = 0;
        const pageLimit = 50; // 每页数量
        let page = 1;

        while (true) {
            if (this.shouldStop) {
                this.log('收到停止请求，中断获取', 'warning');
                break;
            }

            // 先访问对应页面的主页以绕过反爬机制
            if (!await this.visitHomepage(userId, service, offset)) {
                this.log(`无法访问第 ${page} 页的主页，终止操作`, 'error');
                return null;
            }

            // 等待一小段时间，避免请求过快
            await this.sleep(1000);

            // 构造带分页参数的URL
            let apiUrl;
            let userUrl;
            if (offset === 0) {
                apiUrl = `${this.baseUrl}/api/v1/${service}/user/${userId}/posts`;
                userUrl = `${this.baseUrl}/${service}/user/${userId}`;
            } else {
                apiUrl = `${this.baseUrl}/api/v1/${service}/user/${userId}/posts?o=${offset}`;
                userUrl = `${this.baseUrl}/${service}/user/${userId}?o=${offset}`;
            }

            this.log(`正在获取第 ${page} 页数据: ${apiUrl}`);

            try {
                const response = await this._makeRequest(apiUrl, userUrl);
                this.log(`第 ${page} 页API访问状态码: ${response.statusCode}`);

                if (response.statusCode === 200) {
                    const data = JSON.parse(response.data);

                    // 如果没有更多数据，跳出循环
                    if (!data || data.length === 0) {
                        this.log(`第 ${page} 页无数据，结束获取`);
                        break;
                    }

                    allPosts.push(...data);
                    this.log(`第 ${page} 页获取到 ${data.length} 条数据`);

                    // 如果设置了总数限制且已达到限制，则停止
                    if (limit && allPosts.length >= limit) {
                        this.log(`已达到设置的限制数量 ${limit}，结束获取`);
                        return allPosts.slice(0, limit);
                    }

                    // 更新偏移量和页码
                    offset += pageLimit;
                    page += 1;
                } else {
                    this.log(`第 ${page} 页API请求失败，状态码: ${response.statusCode}`, 'error');
                    break;
                }
            } catch (error) {
                this.log(`获取第 ${page} 页数据时出现错误: ${error.message}`, 'error');
                break;
            }
        }

        return allPosts;
    }

    // 逐页查找单个作品
    async getSinglePost(service, userId, postId) {
        let offset = 0;
        const pageLimit = 50;
        let page = 1;
        const targetPostId = String(postId);

        while (true) {
            if (this.shouldStop) {
                this.log('收到停止请求，中断查找单作品', 'warning');
                return null;
            }

            if (!await this.visitHomepage(userId, service, offset)) {
                this.log(`无法访问第 ${page} 页的主页，终止单作品查找`, 'error');
                return null;
            }

            await this.sleep(1000);

            let apiUrl;
            let userUrl;
            if (offset === 0) {
                apiUrl = `${this.baseUrl}/api/v1/${service}/user/${userId}/posts`;
                userUrl = `${this.baseUrl}/${service}/user/${userId}`;
            } else {
                apiUrl = `${this.baseUrl}/api/v1/${service}/user/${userId}/posts?o=${offset}`;
                userUrl = `${this.baseUrl}/${service}/user/${userId}?o=${offset}`;
            }

            this.log(`正在第 ${page} 页查找作品 ${targetPostId}: ${apiUrl}`);

            try {
                const response = await this._makeRequest(apiUrl, userUrl);
                this.log(`第 ${page} 页API访问状态码: ${response.statusCode}`);

                if (response.statusCode !== 200) {
                    this.log(`第 ${page} 页API请求失败，状态码: ${response.statusCode}`, 'error');
                    return null;
                }

                const data = JSON.parse(response.data);
                if (!data || data.length === 0) {
                    this.log(`第 ${page} 页无数据，未找到作品 ${targetPostId}`, 'warning');
                    return null;
                }

                const targetPost = data.find(post => String(post.id) === targetPostId);
                if (targetPost) {
                    this.log(`已找到目标作品: ${targetPostId}`, 'success');
                    return targetPost;
                }

                if (data.length < pageLimit) {
                    this.log(`已遍历完所有分页，未找到作品 ${targetPostId}`, 'warning');
                    return null;
                }

                offset += pageLimit;
                page += 1;
            } catch (error) {
                this.log(`查找作品 ${targetPostId} 时出现错误: ${error.message}`, 'error');
                return null;
            }
        }
    }

    // 清理文件名中的非法字符（对应Python的sanitize_filename）
    sanitizeFilename(filename) {
        // 移除或替换文件名中的非法字符
        const illegalChars = /[<>:"/\\|?*\x00-\x1F]/g;
        filename = filename.replace(illegalChars, '_');
        // 限制文件名长度
        if (filename.length > 150) {
            filename = filename.substring(0, 150);
        }
        return filename.trim();
    }

    // 获取作品元数据
    getPostMetadata(post) {
        const postId = String(post.id || 'unknown');
        const postTitle = (post.title || `post_${postId}`).trim() || `post_${postId}`;
        const published = post.published || '';

        let postDate = 'unknown';
        if (published) {
            postDate = published.split('T')[0];
        }

        const cleanTitle = this.sanitizeFilename(postTitle) || `post_${postId}`;
        const postFolderName = `${postDate} ${cleanTitle}`;
        const postKey = `${postDate}_${cleanTitle}_${postId}`;

        return {
            postId,
            postTitle,
            postDate,
            cleanTitle,
            postFolderName,
            postKey
        };
    }

    // 创建作者目录结构
    async createAuthorDirectories(savePath, authorName) {
        const authorDir = `${savePath}\\${authorName}`;
        const jsonDir = `${authorDir}\\json`;
        const srcDir = `${authorDir}\\src`;

        await window.electronAPI.fs.mkdir(jsonDir);
        await window.electronAPI.fs.mkdir(srcDir);
        this.log(`已创建目录结构: ${authorDir}\\{json,src}`, 'success');

        return { authorDir, jsonDir, srcDir };
    }

    // 分页保存作品数据
    async savePostsPages(jsonDir, postsData) {
        const pageSize = 50;
        const totalPosts = postsData.length;
        const totalPages = Math.ceil(totalPosts / pageSize);

        for (let page = 1; page <= totalPages; page++) {
            const startIdx = (page - 1) * pageSize;
            const endIdx = Math.min(page * pageSize, totalPosts);
            const pageData = postsData.slice(startIdx, endIdx);

            const filename = `${jsonDir}\\${page}.json`;
            await window.electronAPI.fs.write(filename, JSON.stringify(pageData, null, 2));
            this.log(`第 ${page} 页数据已保存到 ${page}.json，共 ${pageData.length} 条记录`);
        }
    }

    // 下载文件（对应Python的download_file）
    async downloadFile(url, filename, folder = 'downloads') {
        try {
            // 确保文件夹存在
            await window.electronAPI.fs.mkdir(folder);

            // 构造完整路径
            const filepath = `${folder}\\${filename}`;

            // 如果文件已存在，跳过下载
            const exists = await window.electronAPI.fs.exists(filepath);
            if (exists) {
                this.log(`文件已存在，跳过下载: ${filename}`, 'info');
                return true;
            }

            this.log(`正在下载: ${filename}`);

            // 处理相对URL
            if (url.startsWith('/')) {
                url = `${this.baseUrl}${url}`;
            }

            // 下载文件
            await window.electronAPI.fs.download(url, filepath);
            this.log(`下载完成: ${filename}`, 'success');
            return true;
        } catch (error) {
            this.log(`下载文件时出现错误 ${filename}: ${error.message}`, 'error');
            return false;
        }
    }

    // 加载下载进度（对应Python的load_progress）
    async loadProgress(progressFile) {
        try {
            const exists = await window.electronAPI.fs.exists(progressFile);
            if (exists) {
                const data = await window.electronAPI.fs.read(progressFile);
                return JSON.parse(data);
            }
        } catch (error) {
            this.log(`加载进度文件失败: ${error.message}`, 'warning');
        }
        return {};
    }

    // 保存下载进度（对应Python的save_progress）
    async saveProgress(progressFile, progressData) {
        try {
            await window.electronAPI.fs.write(
                progressFile,
                JSON.stringify(progressData, null, 2)
            );
        } catch (error) {
            this.log(`保存进度文件失败: ${error.message}`, 'warning');
        }
    }

    // 处理单个作品下载
    async processPostDownload(post, options) {
        const {
            srcDir,
            progressData,
            progressFile,
            concurrent = 5,
            skipExisting = true
        } = options;

        const { postId, postTitle, postDate, postFolderName, postKey } = this.getPostMetadata(post);
        const postFolder = `${srcDir}\\${postFolderName}`;

        if (skipExisting && progressData[postKey]) {
            this.log(`作品 ${postFolderName} 已处理过，跳过...`);
            return {
                status: 'skipped',
                downloadedCount: 0,
                postFolderName,
                postKey
            };
        }

        const attachments = Array.isArray(post.attachments) ? post.attachments : [];
        const downloadTasks = attachments
            .map((attachment, index) => ({ attachment, index }))
            .filter(({ attachment }) => attachment && attachment.path && attachment.name);

        let downloadedAttachments = 0;

        if (downloadTasks.length === 0) {
            this.log(`作品 ${postFolderName} 没有可下载的附件`, 'info');
        }

        for (let i = 0; i < downloadTasks.length; i += concurrent) {
            if (this.shouldStop) {
                this.log(`作品 ${postFolderName} 下载已中断`, 'warning');
                return {
                    status: 'stopped',
                    downloadedCount: downloadedAttachments,
                    postFolderName,
                    postKey
                };
            }

            const batch = downloadTasks.slice(i, i + concurrent);
            const results = await Promise.all(batch.map(({ attachment, index }) => {
                const extensionIndex = attachment.name.lastIndexOf('.');
                const fileExtension = extensionIndex >= 0 ? attachment.name.substring(extensionIndex) : '';
                const filename = `${index + 1}${fileExtension}`;
                return this.downloadFile(attachment.path, filename, postFolder);
            }));

            downloadedAttachments += results.filter(Boolean).length;
        }

        if (downloadedAttachments !== downloadTasks.length) {
            this.log(`作品 ${postFolderName} 有 ${downloadTasks.length - downloadedAttachments} 个附件下载失败，将不会标记为已完成`, 'warning');
            return {
                status: 'partial',
                downloadedCount: downloadedAttachments,
                postFolderName,
                postKey
            };
        }

        progressData[postKey] = {
            post_id: postId,
            post_title: postTitle,
            post_date: postDate,
            attachments_count: attachments.length,
            downloaded_attachments: downloadedAttachments,
            completed_time: new Date().toISOString()
        };

        await this.saveProgress(progressFile, progressData);

        if (attachments.length > 0) {
            this.log(`作品 ${postFolderName} 下载完成，共下载 ${downloadedAttachments} 个附件`, 'success');
        } else {
            this.log(`作品 ${postFolderName} 已处理完成，无附件可下载`, 'success');
        }

        return {
            status: 'completed',
            downloadedCount: downloadedAttachments,
            postFolderName,
            postKey
        };
    }

    // 主执行函数（对应Python的run方法）
    async startScraping(config) {
        if (this.isRunning) {
            this.log('任务已在运行中', 'warning');
            return;
        }

        this.isRunning = true;
        this.shouldStop = false;
        this.currentTask = config;

        try {
            const mode = config.mode || 'author';
            const savePath = config.savePath;
            const concurrent = config.concurrent || 5;
            const skipExisting = config.skipExisting !== false;

            this.log('========== 开始爬取任务 ==========', 'info');
            this.log(`任务模式: ${mode === 'single-post' ? '单作品下载' : '作者全部作品下载'}`, 'info');
            this.log(`保存路径: ${savePath}`, 'info');

            if (mode === 'single-post') {
                const { service, userId, postId, postUrl } = this.parsePostUrl(config.postUrl);

                this.log(`服务平台: ${service}`, 'info');
                this.log(`用户ID: ${userId}`, 'info');
                this.log(`作品ID: ${postId}`, 'info');
                this.log(`作品链接: ${postUrl}`, 'info');
                this.log('限制数量在单作品模式下已忽略', 'info');

                this.log('开始获取用户资料...', 'info');
                const userProfile = await this.getUserProfile(userId, service);
                if (!userProfile) {
                    this.log('获取用户资料失败', 'error');
                    return;
                }

                const authorName = userProfile.public_id || `user_${userId}`;
                this.log(`作者名称: ${authorName}`, 'success');

                const { authorDir, jsonDir, srcDir } = await this.createAuthorDirectories(savePath, authorName);
                const progressFile = `${authorDir}\\download_progress.json`;
                const progressData = await this.loadProgress(progressFile);
                progressData.config = {
                    ...config,
                    mode: 'single-post',
                    service,
                    postUrl
                };
                await this.saveProgress(progressFile, progressData);

                this.log('开始获取单作品数据...', 'info');
                const post = await this.getSinglePost(service, userId, postId);
                if (!post) {
                    if (this.shouldStop) {
                        this.log('任务已停止', 'warning');
                    } else {
                        this.log(`未找到 ID 为 ${postId} 的作品`, 'warning');
                    }
                    return;
                }

                const singlePostFile = `${jsonDir}\\post_${postId}.json`;
                await window.electronAPI.fs.write(singlePostFile, JSON.stringify(post, null, 2));
                this.log(`作品数据已保存到 post_${postId}.json`, 'success');

                if (this.shouldStop) {
                    this.log('任务已停止', 'warning');
                    return;
                }

                this.log('开始下载附件...', 'info');
                this.updateProgress(0, 1, `post/${postId}`);

                const result = await this.processPostDownload(post, {
                    srcDir,
                    progressData,
                    progressFile,
                    concurrent,
                    skipExisting
                });

                if (result.status === 'stopped') {
                    this.log('任务已停止', 'warning');
                    return;
                }

                this.updateProgress(1, 1, result.postFolderName);

                if (result.status === 'partial') {
                    this.log(`作品 ${result.postFolderName} 存在未完成附件，可重新运行以重试`, 'warning');
                }

                this.log(`下载完成: ${result.status === 'skipped' ? 0 : 1} 个作品已处理，${result.status === 'skipped' ? 1 : 0} 个作品已跳过`, 'success');
                this.log(`总共下载了 ${result.downloadedCount} 个文件`, 'success');
                this.log('========== 爬取任务完成 ==========', 'success');
                return;
            }

            const service = config.service;
            const userId = config.username;
            const limit = config.limit;

            this.log(`服务平台: ${service}`, 'info');
            this.log(`用户ID: ${userId}`, 'info');
            if (limit > 0) {
                this.log(`限制数量: ${limit}`, 'info');
            }

            // 1. 获取用户资料
            this.log('开始获取用户资料...', 'info');
            const userProfile = await this.getUserProfile(userId, service);

            if (!userProfile) {
                this.log('获取用户资料失败', 'error');
                return;
            }

            // 获取作者名称
            const authorName = userProfile.public_id || `user_${userId}`;
            this.log(`作者名称: ${authorName}`, 'success');

            // 创建作者目录结构
            const { authorDir, jsonDir, srcDir } = await this.createAuthorDirectories(savePath, authorName);

            // 进度文件路径
            const progressFile = `${authorDir}\\download_progress.json`;

            // 加载下载进度
            const progressData = await this.loadProgress(progressFile);
            progressData.config = {
                ...config,
                mode: 'author'
            };
            await this.saveProgress(progressFile, progressData);

            // 2. 获取用户作品数据
            this.log('开始获取用户作品数据...', 'info');
            const postsData = await this.getUserPosts(userId, service, limit);

            if (!postsData || postsData.length === 0) {
                if (this.shouldStop) {
                    this.log('任务已停止', 'warning');
                } else {
                    this.log('未获取到任何数据', 'warning');
                }
                return;
            }

            this.log(`成功获取到数据，共${postsData.length}条记录`, 'success');

            // 分页保存数据到文件（在json目录中）
            await this.savePostsPages(jsonDir, postsData);

            // 3. 下载附件
            if (this.shouldStop) {
                this.log('任务已停止', 'warning');
                return;
            }

            this.log('开始下载附件...', 'info');

            let completedCount = 0;
            let skippedCount = 0;
            let totalDownloaded = 0;
            const totalPosts = postsData.length;

            for (let i = 0; i < postsData.length; i++) {
                if (this.shouldStop) {
                    this.log('任务已停止', 'warning');
                    break;
                }

                const post = postsData[i];
                const { postFolderName } = this.getPostMetadata(post);
                this.updateProgress(i, totalPosts, postFolderName);

                const result = await this.processPostDownload(post, {
                    srcDir,
                    progressData,
                    progressFile,
                    concurrent,
                    skipExisting
                });

                totalDownloaded += result.downloadedCount;

                if (result.status === 'completed') {
                    completedCount++;
                } else if (result.status === 'skipped') {
                    skippedCount++;
                } else if (result.status === 'partial') {
                    this.log(`作品 ${result.postFolderName} 存在未完成附件，可重新运行以重试`, 'warning');
                } else if (result.status === 'stopped') {
                    break;
                }
            }

            if (this.shouldStop) {
                this.log('任务已停止', 'warning');
                return;
            }

            this.updateProgress(totalPosts, totalPosts, '已完成');
            this.log(`下载完成: ${completedCount} 个作品已处理，${skippedCount} 个作品已跳过`, 'success');
            this.log(`总共下载了 ${totalDownloaded} 个文件`, 'success');
            this.log('========== 爬取任务完成 ==========', 'success');
        } catch (error) {
            this.log(`爬取任务失败: ${error.message}`, 'error');
            console.error(error);
        } finally {
            this.isRunning = false;
            this.shouldStop = false;
            this.currentTask = null;
        }
    }

    // 停止爬取
    stop() {
        if (this.isRunning) {
            this.shouldStop = true;
            this.log('正在停止任务...', 'warning');
        }
    }

    // 从进度文件恢复
    async resumeFromProgress(progressFile) {
        try {
            const progressData = await this.loadProgress(progressFile);
            const config = progressData.config;

            if (!config) {
                throw new Error('进度文件缺少任务配置，无法恢复');
            }

            this.log('从进度文件恢复任务', 'info');
            await this.startScraping(config);
        } catch (error) {
            this.log(`恢复进度失败: ${error.message}`, 'error');
        }
    }
}

// 导出全局实例
window.scraper = new KemonoScraper();
