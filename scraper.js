// Kemono爬虫核心逻辑 - 完全按照kemono_gui copy.py重构
class KemonoScraper {
    constructor() {
        this.baseUrl = 'https://kemono.cr';
        this.isRunning = false;
        this.shouldStop = false;
        this.currentTask = null;
        this.activeDownloads = new Set(); // 正在下载的文件路径
        this._sessionCache = {}; // session 缓存: key → { timestamp, success }
        this._sessionCacheTTL = 5 * 60 * 1000; // session 缓存有效期 5 分钟
        this._adaptiveConcurrent = null; // 自适应并发数（运行时动态调整）
        this._rateLimitHits = 0; // 连续触发限流计数
        this._rateLimitRecovery = 0; // 连续正常计数（用于恢复并发）
        this._incrementalMode = false; // 是否为增量下载模式
        this._incrementalStats = null; // 增量下载统计 { completedCount, totalCount }

        // 常量
        this.PAGE_LIMIT = 50;
        this.MAX_CONSECUTIVE_ERRORS = 3;
        this.POSTS_PER_JSON_FILE = 50;
        this.MAX_LOG_ENTRIES = 200;
        this.CORRUPTED_FILE_SIZE_LIMIT_KB = 100;

        // 全局设置
        this.settings = {
            retries: 5,
            imageTimeout: 60,
            videoTimeout: 1200,
            pageRequestDelay: 1500,
            defaultConcurrent: 5,
            batchDelay: 500,
            skipExistingDefault: true
        };

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

    // 跨平台路径拼接
    joinPath(...parts) {
        return parts.join('/');
    }

    normalizePath(pathValue) {
        return String(pathValue || '').replace(/\\/g, '/');
    }

    // 安全 JSON 解析，解析失败时返回 fallback 值而非抛出异常
    _safeJsonParse(data, fallback = null) {
        try {
            return JSON.parse(data);
        } catch (error) {
            this.log(`JSON 解析失败: ${error.message}`, 'warning');
            return fallback;
        }
    }

    // 创建请求对象（对应Python的_create_request）
    _createRequest(url, referer = null) {
        const requestHeaders = { ...this.headers };
        if (referer) {
            requestHeaders['Referer'] = referer;
        }
        return requestHeaders;
    }

    // 带随机抖动的指数退避延迟
    _jitteredBackoff(attempt, baseMs = 1000) {
        const exponential = baseMs * Math.pow(2, attempt - 1);
        return Math.floor(exponential * (0.5 + Math.random())); // [0.5x, 1.5x] 随机抖动
    }

    // 发起HTTP请求
    async _makeRequest(url, referer = null, retries = null) {
        retries = retries ?? this.settings.retries ?? 5;
        for (let attempt = 1; attempt <= retries; attempt++) {
            try {
                const headers = this._createRequest(url, referer);
                const response = await window.electronAPI.httpRequest({
                    url: url,
                    method: 'GET',
                    headers: headers,
                    timeout: this.settings.requestTimeout || 30000
                });

                if (response.statusCode === 200) {
                    this._rateLimitRecovery++;
                    // 连续 2 次正常请求后，逐步恢复并发数
                    if (this._rateLimitRecovery >= 2 && this._adaptiveConcurrent !== null) {
                        this._recoverConcurrency();
                    }
                    return { statusCode: 200, data: response.data };
                } else if (response.statusCode === 429) {
                    this._rateLimitHits++;
                    this._rateLimitRecovery = 0;
                    this._reduceConcurrency();
                    const delayMs = this._jitteredBackoff(attempt + 1, 1000);
                    this.log(`收到限流响应 (${attempt}/${retries}): 429，${delayMs}ms后重试，并发降至 ${this._adaptiveConcurrent}`, 'warning');
                    await this.sleep(delayMs);
                } else {
                    return { statusCode: response.statusCode, data: null };
                }
            } catch (error) {
                if (attempt < retries) {
                    const delayMs = this._jitteredBackoff(attempt, 1000);
                    this.log(`请求失败 (${attempt}/${retries}): ${error.message}，${delayMs}ms后重试...`, 'warning');
                    await this.sleep(delayMs);
                } else {
                    this.log(`请求失败，已达最大重试次数: ${error.message}`, 'error');
                    throw error;
                }
            }
        }
    }

    // 降低自适应并发数（触发限流时调用）
    _reduceConcurrency() {
        if (this._adaptiveConcurrent === null) return;
        const newVal = Math.max(1, Math.floor(this._adaptiveConcurrent / 2));
        if (newVal < this._adaptiveConcurrent) {
            this._adaptiveConcurrent = newVal;
        }
    }

    // 恢复自适应并发数（连续正常时调用）
    _recoverConcurrency() {
        if (this._adaptiveConcurrent === null || !this.currentTask) return;
        const original = this.currentTask.concurrent || this.settings.defaultConcurrent || 5;
        const newVal = Math.min(original, this._adaptiveConcurrent + 1);
        if (newVal > this._adaptiveConcurrent) {
            this._adaptiveConcurrent = newVal;
            this.log(`并发数恢复至 ${this._adaptiveConcurrent}`, 'info');
        }
        this._rateLimitRecovery = 0;
    }

    // 访问用户主页以设置必要的cookies和referer（对应Python的visit_homepage）
    async visitHomepage(userId, service = 'fanbox', offset = 0) {
        const cacheKey = `${service}:${userId}`;
        const cached = this._sessionCache[cacheKey];

        // 检查缓存是否有效（5分钟内）
        if (cached && (Date.now() - cached.timestamp) < this._sessionCacheTTL) {
            this.log(`使用缓存的 session: ${service}/${userId}`);
            return cached.success;
        }

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
            const success = response.statusCode === 200;
            this._sessionCache[cacheKey] = { timestamp: Date.now(), success };
            return success;
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
        await this.sleep(this.settings.pageRequestDelay || 1000);

        // 访问API获取用户资料
        const profileUrl = `${this.baseUrl}/api/v1/${service}/user/${userId}/profile`;
        const userUrl = `${this.baseUrl}/${service}/user/${userId}`; // 用于设置referer
        this.log(`正在获取用户资料: ${profileUrl}`);

        try {
            const response = await this._makeRequest(profileUrl, userUrl);
            this.log(`用户资料访问状态码: ${response.statusCode}`);

            if (response.statusCode === 200) {
                const profile = this._safeJsonParse(response.data, null);
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
        const pageLimit = this.PAGE_LIMIT; // 每页数量
        let page = 1;
        let consecutiveErrors = 0; // 连续错误计数
        const maxConsecutiveErrors = this.MAX_CONSECUTIVE_ERRORS; // 最多允许连续错误次数

        while (true) {
            if (this.shouldStop) {
                this.log('收到停止请求，中断获取', 'warning');
                break;
            }

            // 先访问对应页面的主页以绕过反爬机制
            try {
                if (!await this.visitHomepage(userId, service, offset)) {
                    this.log(`无法访问第 ${page} 页的主页，终止操作`, 'error');
                    return allPosts.length > 0 ? allPosts : null;
                }
            } catch (error) {
                this.log(`访问主页出错: ${error.message}，跳过此页`, 'warning');
                consecutiveErrors++;
                if (consecutiveErrors >= maxConsecutiveErrors) {
                    this.log(`连续错误超过${maxConsecutiveErrors}次，停止获取`, 'error');
                    break;
                }
                offset += pageLimit;
                page += 1;
                continue;
            }

            // 等待一小段时间，避免请求过快
            await this.sleep(this.settings.pageRequestDelay || 1500);

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
                    const data = this._safeJsonParse(response.data, null);
                    if (data === null) {
                        this.log(`第 ${page} 页数据解析失败，跳过此页`, 'warning');
                        consecutiveErrors++;
                        offset += pageLimit;
                        page += 1;
                        continue;
                    }

                    if (!data || data.length === 0) {
                        this.log(`第 ${page} 页无数据，结束获取`);
                        break;
                    }

                    allPosts.push(...data);
                    this.log(`第 ${page} 页获取到 ${data.length} 条数据，累计 ${allPosts.length} 条`);
                    consecutiveErrors = 0; // 重置错误计数

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
                    consecutiveErrors++;
                    if (consecutiveErrors >= maxConsecutiveErrors) {
                        this.log(`连续错误超过${maxConsecutiveErrors}次，停止获取`, 'error');
                        break;
                    }
                    offset += pageLimit;
                    page += 1;
                }
            } catch (error) {
                this.log(`获取第 ${page} 页数据时出现错误: ${error.message}`, 'error');
                consecutiveErrors++;
                if (consecutiveErrors >= maxConsecutiveErrors) {
                    this.log(`连续错误超过${maxConsecutiveErrors}次，停止获取`, 'error');
                    break;
                }
                offset += pageLimit;
                page += 1;
            }
        }

        this.log(`共获取 ${allPosts.length} 条作品数据`);
        return allPosts.length > 0 ? allPosts : null;
    }

    // 逐页查找单个作品
    async getSinglePost(service, userId, postId) {
        let offset = 0;
        const pageLimit = this.PAGE_LIMIT;
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

            await this.sleep(this.settings.pageRequestDelay || 1000);

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

                const data = this._safeJsonParse(response.data, null);
                if (data === null) {
                    this.log(`作品查找页数据解析失败`, 'warning');
                    return null;
                }
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
        const authorDir = this.joinPath(savePath, authorName);
        const jsonDir = this.joinPath(authorDir, 'json');
        const srcDir = this.joinPath(authorDir, 'src');

        await window.electronAPI.fs.mkdir(jsonDir);
        await window.electronAPI.fs.mkdir(srcDir);
        this.log(`已创建目录结构: ${authorDir}/{json,src}`, 'success');

        return { authorDir, jsonDir, srcDir };
    }

    // 分页保存作品数据
    async savePostsPages(jsonDir, postsData) {
        const pageSize = this.POSTS_PER_JSON_FILE;
        const totalPosts = postsData.length;
        const totalPages = Math.ceil(totalPosts / pageSize);

        for (let page = 1; page <= totalPages; page++) {
            const startIdx = (page - 1) * pageSize;
            const endIdx = Math.min(page * pageSize, totalPosts);
            const pageData = postsData.slice(startIdx, endIdx);

            const filename = this.joinPath(jsonDir, `${page}.json`);
            await window.electronAPI.fs.write(filename, JSON.stringify(pageData, null, 2));
            this.log(`第 ${page} 页数据已保存到 ${page}.json，共 ${pageData.length} 条记录`);
        }
    }

    // 检测文件类型并返回超时时间（毫秒）
    getTimeoutForFile(filename) {
        const imageExtensions = ['.jpg', '.jpeg', '.png', '.gif', '.webp', '.bmp', '.tiff', '.svg'];
        const videoExtensions = ['.mp4', '.webm', '.avi', '.mov', '.mkv', '.flv', '.wmv', '.m4v'];
        
        const lowerFilename = filename.toLowerCase();
        
        for (const ext of imageExtensions) {
            if (lowerFilename.endsWith(ext)) {
                return (this.settings.imageTimeout || 60) * 1000; // 图片超时
            }
        }
        
        for (const ext of videoExtensions) {
            if (lowerFilename.endsWith(ext)) {
                return (this.settings.videoTimeout || 1200) * 1000; // 视频超时
            }
        }
        
        return 0; // 其他类型不限制
    }

    // 下载文件（对应Python的download_file）
    async downloadFile(url, filename, folder = 'downloads') {
        let filepath;
        try {
            // 确保文件夹存在
            await window.electronAPI.fs.mkdir(folder);

            // 构造完整路径
            filepath = this.joinPath(folder, filename);

            // 如果文件已存在且不是明显的残缺文件，跳过下载
            const exists = await window.electronAPI.fs.exists(filepath);
            if (exists) {
                const stats = await window.electronAPI.fs.stat(filepath);
                if (stats.isFile && stats.size > 0) {
                    this.log(`文件已存在，跳过下载: ${filename}`, 'info');
                    return true;
                }

                this.log(`发现空文件，将重新下载: ${filename}`, 'warning');
                await window.electronAPI.fs.deleteFile(filepath);
            }

            this.log(`正在下载: ${filename}`);

            // 处理相对URL
            if (url.startsWith('/')) {
                url = `${this.baseUrl}${url}`;
            }

            // 获取文件类型对应的超时时间
            const timeoutMs = this.getTimeoutForFile(filename);

            // 追踪正在下载的文件
            this.activeDownloads.add(filepath);

            // 下载文件（支持超时机制）
            await window.electronAPI.fs.download(url, filepath, timeoutMs);

            this.activeDownloads.delete(filepath);
            this.log(`下载完成: ${filename}`, 'success');
            return true;
        } catch (error) {
            if (filepath) this.activeDownloads.delete(filepath);
            const errorMsg = error.message || String(error);

            // 判断错误类型
            if (errorMsg.includes('超时')) {
                this.log(`下载超时已跳过: ${filename}`, 'warning');
                return false;
            } else if (errorMsg.includes('ECONNRESET') || errorMsg.includes('ETIMEDOUT') || errorMsg.includes('aborted')
                || errorMsg.includes('ERR_CONNECTION_') || errorMsg.includes('ERR_TIMED_OUT') || errorMsg.includes('ERR_ABORTED')
                || errorMsg.includes('ERR_NAME_NOT_RESOLVED') || errorMsg.includes('中止')) {
                this.log(`网络连接中断已跳过: ${filename}`, 'warning');
                return false;
            } else if (errorMsg.includes('EACCES') || errorMsg.includes('EPERM')) {
                // 文件权限错误
                this.log(`文件权限错误（跳过）: ${filename}`, 'warning');
                return false;
            } else {
                // 其他错误
                this.log(`下载文件时出现错误 ${filename}: ${errorMsg}`, 'error');
                return false;
            }
        }
    }

    // 加载下载进度（对应Python的load_progress）
    async loadProgress(progressFile) {
        try {
            const exists = await window.electronAPI.fs.exists(progressFile);
            if (exists) {
                const data = await window.electronAPI.fs.read(progressFile);
                return this._safeJsonParse(data, {});
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
            skipExisting = true,
            batchDelay = 500,
            useThumbnail = false
        } = options;

        const { postId, postTitle, postDate, postFolderName, postKey } = this.getPostMetadata(post);
        const postFolder = this.joinPath(srcDir, postFolderName);

        if (skipExisting && progressData[postKey]?.completed_time) {
            this.log(`作品 ${postFolderName} 已处理过，跳过...`);
            return {
                status: 'skipped',
                downloadedCount: 0,
                postFolderName,
                postKey
            };
        }

        // Thumbnail mode: fetch post detail API to get preview URLs (img.kemono.cr)
        let thumbnailMap = null;
        if (useThumbnail) {
            const service = post.service || (this.currentTask && this.currentTask.service);
            const userId = post.user || (this.currentTask && this.currentTask.username);
            if (service && userId) {
                const postPageUrl = `${this.baseUrl}/${service}/user/${userId}/post/${postId}`;
                const postDetailUrl = `${this.baseUrl}/api/v1/${service}/user/${userId}/post/${postId}`;
                try {
                    this.log(`访问作品页面: ${postPageUrl}`);
                    await this._makeRequest(postPageUrl);
                    await this.sleep(this.settings.pageRequestDelay || 1000);

                    this.log(`获取缩略图数据: ${postDetailUrl}`);
                    const response = await this._makeRequest(postDetailUrl, postPageUrl);
                    if (response && response.statusCode === 200) {
                        const detail = this._safeJsonParse(response.data, null);
                        const previews = (detail && detail.previews) ? detail.previews : [];
                        thumbnailMap = new Map();
                        for (const preview of previews) {
                            if (preview.type === 'thumbnail' && preview.path && preview.name) {
                                if (!thumbnailMap.has(preview.name)) {
                                    thumbnailMap.set(preview.name, `https://kemono.cr/thumbnail/data${preview.path}`);
                                }
                            }
                        }
                        this.log(`获取到 ${thumbnailMap.size} 个缩略图`, 'success');
                    } else {
                        this.log(`获取缩略图失败，状态码: ${response ? response.statusCode : 'unknown'}`, 'warning');
                    }
                } catch (error) {
                    this.log(`获取缩略图出错: ${error.message}`, 'warning');
                }
            } else {
                this.log('缺少 service/userId 信息，无法获取缩略图', 'warning');
            }
        }

        const attachments = Array.isArray(post.attachments) ? post.attachments : [];
        const downloadTasks = [];
        const seenNames = new Set();

        // Include main file (post.file) as index 0 if present
        if (post.file && post.file.path && post.file.name) {
            downloadTasks.push({ attachment: post.file, index: 0 });
            seenNames.add(post.file.name);
        }

        // Include attachments, deduplicating by name against the main file
        attachments
            .filter(a => a && a.path && a.name)
            .forEach((attachment) => {
                if (!seenNames.has(attachment.name)) {
                    seenNames.add(attachment.name);
                    downloadTasks.push({ attachment, index: downloadTasks.length });
                }
            });

        let downloadedAttachments = 0;

        if (downloadTasks.length === 0) {
            this.log(`作品 ${postFolderName} 没有可下载的附件`, 'info');
        }

        const batchSize = this._adaptiveConcurrent || concurrent;
        for (let i = 0; i < downloadTasks.length; i += batchSize) {
            if (this.shouldStop) {
                this.log(`作品 ${postFolderName} 下载已中断`, 'warning');
                return {
                    status: 'stopped',
                    downloadedCount: downloadedAttachments,
                    postFolderName,
                    postKey
                };
            }

            const batch = downloadTasks.slice(i, i + batchSize);
            try {
                const results = await Promise.allSettled(batch.map(async ({ attachment, index }, staggerIndex) => {
                    if (staggerIndex > 0) {
                        await this.sleep(staggerIndex * 100);
                    }
                    const extensionIndex = attachment.name.lastIndexOf('.');
                    const fileExtension = extensionIndex >= 0 ? attachment.name.substring(extensionIndex) : '';
                    const filename = `${index + 1}${fileExtension}`;
                    let downloadUrl;
                    if (thumbnailMap) {
                        if (thumbnailMap.has(attachment.name)) {
                            downloadUrl = thumbnailMap.get(attachment.name);
                        } else {
                            return false; // thumbnail not found, skip silently
                        }
                    } else if (useThumbnail) {
                        return false; // thumbnail data unavailable, skip silently
                    } else {
                        downloadUrl = attachment.path;
                        if (!downloadUrl.includes('?f=')) {
                            downloadUrl += '?f=' + encodeURIComponent(attachment.name);
                        }
                    }
                    return this.downloadFile(downloadUrl, filename, postFolder);
                }));

                // 统计成功、超时、失败的文件数
                let successCount = 0;
                let skipCount = 0;
                for (const result of results) {
                    if (result.status === 'fulfilled') {
                        if (result.value) {
                            successCount++;
                        } else {
                            skipCount++; // 超时或其他原因跳过
                        }
                    } else {
                        skipCount++;
                    }
                }
                downloadedAttachments += successCount;
                
                if (skipCount > 0) {
                    this.log(`该批次有 ${skipCount} 个文件被跳过（超时或其他原因）`, 'warning');
                }
            } catch (error) {
                this.log(`处理下载批次时出现错误: ${error.message}`, 'error');
            }

            // 批次间延迟，避免触发限流
            if (i + batchSize < downloadTasks.length && batchDelay > 0) {
                await this.sleep(batchDelay);
            }
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
            attachments_count: downloadTasks.length,
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
        const forceFresh = config.forceFresh === true || config.resetProgress === true;
        this._adaptiveConcurrent = config.concurrent || this.settings.defaultConcurrent || 5;
        this._rateLimitHits = 0;
        this._rateLimitRecovery = 0;

        try {
            const mode = config.mode || 'author';
            const savePath = config.savePath;
            const concurrent = config.concurrent || this.settings.defaultConcurrent || 5;
            const skipExisting = config.skipExisting !== undefined ? config.skipExisting !== false : this.settings.skipExistingDefault !== false;
            const batchDelay = config.batchDelay !== undefined ? config.batchDelay : (this.settings.batchDelay || 500);
            const useThumbnail = config.useThumbnail === true;

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
                const progressFile = this.joinPath(authorDir, 'download_progress.json');
                
                // 加载或重置下载进度（自动检测增量模式）
                let progressData = {};
                this._incrementalMode = false;
                this._incrementalStats = null;
                const progressExists = await window.electronAPI.fs.exists(progressFile);
                if (progressExists && !forceFresh) {
                    progressData = await this.loadProgress(progressFile);
                    let completedCount = 0;
                    for (const key in progressData) {
                        if (key !== 'config' && progressData[key]?.completed_time) completedCount++;
                    }
                    if (completedCount > 0) {
                        this._incrementalMode = true;
                        this._incrementalStats = { completedCount };
                        this.log(`检测到 ${completedCount} 个已完成附件，将跳过`, 'info');
                    } else {
                        this.log('检测到下载记录，将跳过已完成的附件', 'info');
                    }
                } else if (forceFresh) {
                    this.log('用户选择全新下载，重置进度', 'info');
                } else {
                    this.log('首次下载，创建新进度', 'info');
                }

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

                const singlePostFile = this.joinPath(jsonDir, `post_${postId}.json`);
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
                    skipExisting,
                    batchDelay,
                    useThumbnail
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
            const progressFile = this.joinPath(authorDir, 'download_progress.json');

            // 加载或重置下载进度（自动检测增量模式）
            let progressData = {};
            this._incrementalMode = false;
            this._incrementalStats = null;

            const progressExists = await window.electronAPI.fs.exists(progressFile);
            if (progressExists && !forceFresh) {
                // 已有下载记录，加载并进入增量模式
                progressData = await this.loadProgress(progressFile);
                let completedCount = 0;
                for (const key in progressData) {
                    if (key !== 'config' && progressData[key]?.completed_time) completedCount++;
                }
                if (completedCount > 0) {
                    this._incrementalMode = true;
                    this._incrementalStats = { completedCount };
                    this.log(`检测到历史下载记录：${completedCount} 个作品已完成，自动启用增量下载模式`, 'success');
                    if (window.updateIncrementalStatus) {
                        window.updateIncrementalStatus({ incremental: true, completedCount, authorName, savePath, authorDir });
                    }
                } else {
                    this.log('检测到进度文件但无已完成作品，使用全新模式', 'info');
                }
            } else if (forceFresh) {
                this.log('用户选择全新下载，重置进度', 'info');
            } else {
                this.log('首次下载此作者，创建新进度', 'info');
            }

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
                    skipExisting,
                    batchDelay,
                    useThumbnail
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
    async stop() {
        if (this.isRunning) {
            this.shouldStop = true;
            this.log('正在停止任务...', 'warning');

            // 清理正在下载的部分文件
            if (this.activeDownloads.size > 0) {
                this.log(`正在清理 ${this.activeDownloads.size} 个未完成的下载文件...`, 'warning');
                for (const filepath of this.activeDownloads) {
                    try {
                        await window.electronAPI.fs.deleteFile(filepath);
                        this.log(`已清理未完成文件: ${filepath}`, 'info');
                    } catch (e) {
                        // 文件可能已经被删除或不存在，忽略错误
                    }
                }
                this.activeDownloads.clear();
            }
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

    // 应用全局设置
    applyGlobalSettings(settings) {
        this.settings = { ...settings };
        this.log('全局设置已应用', 'info');
    }

    // 扫描损坏的文件
    async scanCorruptedFiles(basePath, fileSizeLimit = 100) {
        this.log(`扫描路径: ${basePath}，文件大小限制: ${fileSizeLimit}KB`, 'info');

        try {
            const results = {
                path: basePath,
                files: [],
                totalCount: 0,
                totalSize: 0,
                corruptedCount: 0
            };

            // 检查json文件夹存在
            const jsonDir = this.joinPath(basePath, 'json');
            const progressFile = this.joinPath(basePath, 'download_progress.json');

            const jsonExists = await window.electronAPI.fs.exists(jsonDir);
            const progressExists = await window.electronAPI.fs.exists(progressFile);

            if (!jsonExists || !progressExists) {
                throw new Error('缺少json文件夹或download_progress.json文件');
            }

            // 读取进度文件获取已下载的信息
            const progressData = await this.loadProgress(progressFile);
            
            // 从所有json文件中扫描src文件夹里的文件
            const srcDir = this.joinPath(basePath, 'src');
            
            // 扫描src目录下的所有作品文件夹
            const results2 = await this.scanDirectory(srcDir, fileSizeLimit);
            
            return results2;
        } catch (error) {
            this.log(`扫描损坏文件失败: ${error.message}`, 'error');
            throw error;
        }
    }

    // 扫描目录
    async scanDirectory(dirPath, fileSizeLimit) {
        const result = {
            path: dirPath,
            files: [],
            totalCount: 0,
            totalSize: 0,
            corruptedCount: 0
        };

        try {
            const files = await window.electronAPI.fs.scanDir(dirPath);
            
            for (const file of files) {
                result.totalCount++;
                result.totalSize += file.size;

                // 检查是否为损坏文件（小于限制大小）
                const fileSizeMB = file.size / (1024 * 1024);
                if (fileSizeMB < (fileSizeLimit / 1024)) { // 转换为MB比较
                    result.corruptedCount++;
                    result.files.push({
                        name: file.name,
                        path: file.path,
                        size: file.size
                    });
                    this.log(`发现损坏文件: ${file.name} (${(file.size / 1024).toFixed(2)}KB)`, 'warning');
                }
            }

            this.log(`扫描完成: 总 ${result.totalCount} 个文件，发现 ${result.files.length} 个损坏文件`, 'info');
            return result;
        } catch (error) {
            this.log(`扫描目录失败: ${error.message}`, 'error');
            throw error;
        }
    }

    // 从JSON文件中匹配损坏附件的URL信息
    async _matchCorruptedAttachments(jsonFiles, srcDir, corruptedFilePaths, allAttachments, isPagedArray) {
        for (const jsonFile of jsonFiles) {
            try {
                const data = await window.electronAPI.fs.read(jsonFile);
                const parsed = this._safeJsonParse(data, null);
                if (parsed === null) continue;
                const posts = isPagedArray ? parsed : [parsed];

                for (const post of posts) {
                    if (!post) continue;

                    const attachments = Array.isArray(post.attachments) ? post.attachments : [];
                    const downloadTasks = [];
                    const seenNames = new Set();

                    if (post.file && post.file.path && post.file.name) {
                        downloadTasks.push({ attachment: post.file, index: 0 });
                        seenNames.add(post.file.name);
                    }

                    attachments
                        .filter(a => a && a.path && a.name)
                        .forEach((attachment) => {
                            if (!seenNames.has(attachment.name)) {
                                seenNames.add(attachment.name);
                                downloadTasks.push({ attachment, index: downloadTasks.length });
                            }
                        });

                    for (const { attachment, index } of downloadTasks) {
                        if (!attachment || !attachment.path || !attachment.name) continue;

                        const { postFolderName } = this.getPostMetadata(post);
                        const postFolder = this.joinPath(srcDir, postFolderName);
                        const extensionIndex = attachment.name.lastIndexOf('.');
                        const fileExtension = extensionIndex >= 0 ? attachment.name.substring(extensionIndex) : '';
                        const filename = `${index + 1}${fileExtension}`;
                        const filePath = this.joinPath(postFolder, filename);

                        if (corruptedFilePaths.has(this.normalizePath(filePath))) {
                            let downloadUrl = attachment.path;
                            if (!downloadUrl.includes('?f=')) {
                                downloadUrl += '?f=' + encodeURIComponent(attachment.name);
                            }
                            allAttachments.push({
                                filePath,
                                url: downloadUrl,
                                filename,
                                postFolder
                            });
                        }
                    }
                }
            } catch (error) {
                this.log(`读取json文件失败: ${jsonFile} - ${error.message}`, 'warning');
            }
        }
    }

    // 修复损坏的文件
    async repairCorruptedFiles(basePath, concurrent = 5, progressFile) {
        if (!progressFile) {
            progressFile = this.joinPath(basePath, 'download_progress.json');
        }

        this.log(`开始修复损坏文件，路径: ${basePath}`, 'info');

        try {
            // 读取进度文件获取配置和已下载信息
            const progressData = await this.loadProgress(progressFile);
            const config = progressData?.config;

            if (!config) {
                throw new Error('进度文件缺少配置信息，无法修复');
            }

            this.log(`读取到配置: 模式=${config.mode}, 服务=${config.service}, 用户=${config.username || '单作品'}`, 'info');

            // 读取所有json文件获取附件信息
            const jsonDir = this.joinPath(basePath, 'json');
            const srcDir = this.joinPath(basePath, 'src');

            const allAttachments = [];

            // 得到所有损坏文件的路径映射
            const corruptedFilePaths = new Set();
            const scanResults2 = await this.scanDirectory(srcDir, this.CORRUPTED_FILE_SIZE_LIMIT_KB);
            for (const file of scanResults2.files) {
                corruptedFilePaths.add(this.normalizePath(file.path));
            }

            this.log(`扫描到 ${corruptedFilePaths.size} 个损坏文件`, 'warning');

            // 读取json文件找出这些文件对应的URL
            if (config.mode === 'author') {
                const jsonFiles = await this.getJsonFiles(jsonDir);
                await this._matchCorruptedAttachments(jsonFiles, srcDir, corruptedFilePaths, allAttachments, true);
            } else if (config.mode === 'single-post') {
                const jsonFiles = await this.getJsonFiles(jsonDir);
                await this._matchCorruptedAttachments(jsonFiles, srcDir, corruptedFilePaths, allAttachments, false);
            }

            this.log(`找到 ${allAttachments.length} 个损坏文件需要重新下载`, 'info');

            // 删除所有损坏文件
            for (const file of scanResults2.files) {
                try {
                    await window.electronAPI.fs.deleteFile(file.path);
                    this.log(`已删除损坏文件: ${file.name}`, 'warning');
                } catch (error) {
                    this.log(`删除文件失败: ${file.name} - ${error.message}`, 'error');
                }
            }

            // 重新下载这些文件
            this.log(`开始重新下载 ${allAttachments.length} 个文件`, 'info');
            
            let redownloadCount = 0;
            for (let i = 0; i < allAttachments.length; i += concurrent) {
                const batch = allAttachments.slice(i, i + concurrent);
                
                const results = await Promise.allSettled(batch.map(async (item, staggerIndex) => {
                    if (staggerIndex > 0) {
                        await this.sleep(staggerIndex * 100);
                    }
                    return this.downloadFile(item.url, item.filename, item.postFolder);
                }));

                for (const result of results) {
                    if (result.status === 'fulfilled' && result.value) {
                        redownloadCount++;
                    }
                }
            }

            this.log(`修复完成: 成功重新下载 ${redownloadCount}/${allAttachments.length} 个文件`, 'success');
            this.log('========== 修复任务完成 ==========', 'success');
        } catch (error) {
            this.log(`修复过程中出错: ${error.message}`, 'error');
            throw error;
        }
    }

    // 获取json文件列表
    async getJsonFiles(jsonDir) {
        try {
            const files = await window.electronAPI.fs.scanDir(jsonDir);
            return files
                .filter(f => f.name.endsWith('.json'))
                .map(f => f.path);
        } catch (error) {
            this.log(`获取json文件列表失败: ${error.message}`, 'error');
            return [];
        }
    }
}

// 导出全局实例
window.scraper = new KemonoScraper();
