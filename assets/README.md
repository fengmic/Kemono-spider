# 应用图标

请将应用图标文件 `icon.ico` 放在此目录下。

## 图标要求

- **格式**: .ico
- **推荐尺寸**: 256x256 或 512x512
- **多尺寸支持**: 建议包含 16x16, 32x32, 48x48, 64x64, 128x128, 256x256

## 在线图标生成工具

如果没有.ico文件，可以使用以下在线工具转换：
- https://www.icoconverter.com/
- https://convertio.co/zh/png-ico/
- https://www.online-convert.com/

## 使用PNG图片

如果只有PNG图片，可以使用以下npm包转换：
```bash
npm install -g png2icons
png2icons your-image.png -o assets/icon.ico
```

## 临时图标

在没有自定义图标的情况下，构建时会使用Electron的默认图标。
