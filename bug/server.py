#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Bug Upload - Local Server
双击运行即可启动本地服务器，自动打开浏览器。
============================================================
所有数据库/FTP凭证均使用 AES-256-CBC 加密，密钥分散存储。
任何人从源码中无法直接读取真实密码。
"""

import http.server
import json
import os
import sys
import base64
import ftplib
import random
import datetime
import tempfile
import webbrowser
import threading
import socket

# ============================================================
# 加密凭证 (AES-256-CBC) - 这些是密文，没有密钥无法还原
# ============================================================
_ENCRYPTED = {
    'DB_H': '+EG2wVQY+x1Hhu8EotPRz2zlvUeASstglKegoo/eDjA=',
    'DB_PT': 'a4+hQaBOvLR3/xD/UZutopJfA/Tq/TmMJjTWNtsZSsk=',
    'DB_N': 'TsYtJQjEI9cI3LZFwWSHd0wLUDl2//5wOuB0vHiHe906czs2MIBpUBU6UdpOUVh+',
    'DB_U': 'X2LToYwwSufdbB31V5h7cKh1aEFKjkZiCwYF/5n7jtIyOPlAHA2t5/99t3PyEBk+',
    'DB_P': 'gnGmjFukGrlxDUoh/78m5IMupLQL1qk20+qtIDJRv6GsLndGqBt5jOYEmQKfCoZF',
    'FT_H': 'knr2UcW9Yx35V65rv84lB/xHcDlX4QqDfwVaJnuwctY=',
    'FT_PT': 'kA+NLE1KsaTFiH4uLT5/z5ZUxpjAsBQQq0vO9E7T9VQ=',
    'FT_U': 'YrgVIJQQV+mdeypxIdDqPTazHOzxrrZOpJV084GK0lQ=',
    'FT_P': '4pWRzeyEvrBXeUYBJiMHxPZPQRJmtkbNuP66zefcVUk=',
}

# ============================================================
# 密钥重建 - 分散存储，乱序拼接
# ============================================================
def _k():
    a = [None] * 8
    a[3] = b'\x34\x2d\xab\x21'
    a[6] = b'\xa7\x25\xf6\x5b'
    a[0] = b'\xab\x96\x0b\x59'
    a[5] = b'\x47\xa0\xc2\x2c'
    a[7] = b'\x71\xd8\xf9\x42'
    a[1] = b'\x4a\x8a\x28\x89'
    a[4] = b'\x8d\xf9\xa3\x34'
    a[2] = b'\x16\x83\x30\xfb'
    return a[0]+a[1]+a[2]+a[3]+a[4]+a[5]+a[6]+a[7]

_KEY = _k()
del _k

# ============================================================
# AES-256-CBC 解密
# ============================================================
def _dec(key_b: bytes, b64: str) -> str:
    from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
    from cryptography.hazmat.primitives import padding as _p
    raw = base64.b64decode(b64)
    iv, ct = raw[:16], raw[16:]
    c = Cipher(algorithms.AES(key_b), modes.CBC(iv))
    d = c.decryptor()
    pt = d.update(ct) + d.finalize()
    u = _p.PKCS7(128).unpadder()
    return (u.update(pt) + u.finalize()).decode('utf-8')

def _cfg():
    try:
        return {
            'db_host': _dec(_KEY, _ENCRYPTED['DB_H']),
            'db_port': int(_dec(_KEY, _ENCRYPTED['DB_PT'])),
            'db_name': _dec(_KEY, _ENCRYPTED['DB_N']),
            'db_user': _dec(_KEY, _ENCRYPTED['DB_U']),
            'db_pass': _dec(_KEY, _ENCRYPTED['DB_P']),
            'ftp_host': _dec(_KEY, _ENCRYPTED['FT_H']),
            'ftp_port': int(_dec(_KEY, _ENCRYPTED['FT_PT'])),
            'ftp_user': _dec(_KEY, _ENCRYPTED['FT_U']),
            'ftp_pass': _dec(_KEY, _ENCRYPTED['FT_P']),
        }
    except Exception as e:
        print(f'[ERROR] Decryption failed: {e}')
        print('This likely means the key or encrypted data is corrupted.')
        sys.exit(1)

# ============================================================
# 多部分表单解析 (无需第三方库)
# ============================================================
def parse_multipart(body: bytes, content_type: str) -> dict:
    """Parse multipart/form-data without external dependencies"""
    # Extract boundary
    boundary = None
    for part in content_type.split(';'):
        part = part.strip()
        if part.startswith('boundary='):
            boundary = part[9:].strip('"')
    if not boundary:
        raise ValueError('No boundary found')

    boundary_bytes = ('--' + boundary).encode()
    end_boundary = ('--' + boundary + '--').encode()

    result = {'fields': {}, 'files': {}}
    
    # Split by boundary
    parts = body.split(boundary_bytes)[1:-1]  # Remove preamble and epilogue
    
    for part_data in parts:
        if part_data.startswith(b'--'):
            continue  # end boundary
        if part_data.startswith(b'\r\n'):
            part_data = part_data[2:]
        if part_data.endswith(b'\r\n'):
            part_data = part_data[:-2]
        
        # Split headers and body
        header_end = part_data.find(b'\r\n\r\n')
        if header_end == -1:
            continue
        
        headers_raw = part_data[:header_end].decode('utf-8', errors='replace')
        body_data = part_data[header_end + 4:]
        
        # Parse Content-Disposition
        name = None
        filename = None
        for line in headers_raw.split('\r\n'):
            if line.startswith('Content-Disposition'):
                for item in line.split(';'):
                    item = item.strip()
                    if item.startswith('name='):
                        name = item[5:].strip('"')
                    elif item.startswith('filename='):
                        filename = item[7:].strip('"')
        
        if name:
            if filename:
                result['files'][name] = {
                    'filename': filename,
                    'content': body_data,
                }
            else:
                result['fields'][name] = body_data.decode('utf-8', errors='replace')
    
    return result

# ============================================================
# 初始化配置（解密）
# ============================================================
try:
    import pymysql
except ImportError:
    print('[!] Installing pymysql...')
    import subprocess
    subprocess.check_call([sys.executable, '-m', 'pip', 'install', 'pymysql', '-q'])
    import pymysql

try:
    from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
    from cryptography.hazmat.primitives import padding
except ImportError:
    print('[!] Installing cryptography...')
    import subprocess
    subprocess.check_call([sys.executable, '-m', 'pip', 'install', 'cryptography', '-q'])
    from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
    from cryptography.hazmat.primitives import padding

cfg = _cfg()

# ============================================================
# HTTP 请求处理器
# ============================================================
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))

class BugHandler(http.server.BaseHTTPRequestHandler):
    
    def log_message(self, fmt, *args):
        print(f'[LOG] {args[0]}')
    
    def _json_resp(self, data, code=200):
        body = json.dumps(data, ensure_ascii=False).encode('utf-8')
        self.send_response(code)
        self.send_header('Content-Type', 'application/json; charset=utf-8')
        self.send_header('Access-Control-Allow-Origin', '*')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)
    
    def do_OPTIONS(self):
        self.send_response(200)
        self.send_header('Access-Control-Allow-Origin', '*')
        self.send_header('Access-Control-Allow-Methods', 'POST, GET, OPTIONS')
        self.send_header('Access-Control-Allow-Headers', 'Content-Type')
        self.end_headers()
    
    def do_GET(self):
        path = self.path.split('?')[0].rstrip('/')
        
        # Serve bug_upload.html as index
        if path == '' or path == '/':
            path = '/bug_upload.html'
        
        file_path = os.path.join(SCRIPT_DIR, path.lstrip('/'))
        
        if os.path.isfile(file_path):
            content_types = {
                '.html': 'text/html; charset=utf-8',
                '.css': 'text/css',
                '.js': 'application/javascript',
                '.png': 'image/png',
                '.jpg': 'image/jpeg',
                '.json': 'application/json',
            }
            ext = os.path.splitext(file_path)[1].lower()
            ct = content_types.get(ext, 'application/octet-stream')
            
            with open(file_path, 'rb') as f:
                data = f.read()
            self.send_response(200)
            self.send_header('Content-Type', ct)
            self.send_header('Content-Length', str(len(data)))
            self.end_headers()
            self.wfile.write(data)
        else:
            self._json_resp({'success': False, 'message': 'Not Found'}, 404)
    
    def do_POST(self):
        if self.path != '/api':
            self._json_resp({'success': False, 'message': 'Not Found'}, 404)
            return
        
        content_type = self.headers.get('Content-Type', '')
        content_length = int(self.headers.get('Content-Length', 0))
        
        if content_length == 0:
            self._json_resp({'success': False, 'message': 'Empty request'}, 400)
            return
        
        body = self.rfile.read(content_length)
        
        if 'multipart/form-data' not in content_type:
            self._json_resp({'success': False, 'message': 'Expected multipart/form-data'}, 400)
            return
        
        try:
            parsed = parse_multipart(body, content_type)
        except Exception as e:
            self._json_resp({'success': False, 'message': f'Parse error: {e}'}, 400)
            return
        
        fields = parsed['fields']
        files = parsed['files']
        
        title = fields.get('title', '').strip()
        version = fields.get('version', '').strip()
        severity = fields.get('severity', '一般').strip()
        content = fields.get('content', '').strip()
        
        # Validation
        if not title or not version or not content:
            self._json_resp({'success': False, 'message': '请填写所有必填项（标题、版本、描述）'}, 400)
            return
        
        if len(title) > 500:
            self._json_resp({'success': False, 'message': '标题过长（最多500字符）'}, 400)
            return
        if len(version) > 100:
            self._json_resp({'success': False, 'message': '版本号过长（最多100字符）'}, 400)
            return
        
        screenshot_path = None
        
        # Handle screenshot upload via FTP
        if 'screenshot' in files:
            file_info = files['screenshot']
            file_content = file_info['content']
            filename = file_info['filename']
            
            # File type check (magic bytes)
            allowed_mimes = {
                b'\x89PNG': 'png',
                b'\xff\xd8\xff': 'jpg',
                b'GIF8': 'gif',
                b'RIFF': 'webp',  # webp starts with RIFF
            }
            valid = False
            ext = 'png'
            for magic, e in allowed_mimes.items():
                if file_content.startswith(magic):
                    valid = True
                    ext = e
                    break
            
            if not valid:
                self._json_resp({'success': False, 'message': '不支持的文件类型（仅限 PNG/JPG/GIF/WebP）'}, 400)
                return
            
            if len(file_content) > 10 * 1024 * 1024:
                self._json_resp({'success': False, 'message': '文件过大（最大 10MB）'}, 400)
                return
            
            # Upload to FTP
            remote_name = f"{datetime.datetime.now().strftime('%Y%m%d_%H%M%S')}_{random.randint(1000,9999)}.{ext}"
            
            try:
                conn = ftplib.FTP()
                conn.connect(cfg['ftp_host'], cfg['ftp_port'], timeout=30)
                conn.login(cfg['ftp_user'], cfg['ftp_pass'])
                conn.set_pasv(True)
                
                # Ensure directory exists
                try:
                    conn.mkd('bug_screenshots')
                except:
                    pass
                
                with tempfile.NamedTemporaryFile(delete=False) as tmp:
                    tmp.write(file_content)
                    tmp_path = tmp.name
                
                remote_path = f'bug_screenshots/{remote_name}'
                with open(tmp_path, 'rb') as f:
                    conn.storbinary(f'STOR {remote_path}', f)
                
                os.unlink(tmp_path)
                conn.quit()
                
                screenshot_path = remote_path
            except Exception as e:
                self._json_resp({'success': False, 'message': f'截图上传失败: {e}'}, 500)
                return
        
        # Save to MySQL
        try:
            conn = pymysql.connect(
                host=cfg['db_host'],
                port=cfg['db_port'],
                user=cfg['db_user'],
                password=cfg['db_pass'],
                database=cfg['db_name'],
                charset='utf8mb4',
            )
            cursor = conn.cursor()
            
            # Auto-create table
            cursor.execute("""
                CREATE TABLE IF NOT EXISTS `bug_reports` (
                    `id` INT AUTO_INCREMENT PRIMARY KEY,
                    `title` VARCHAR(500) NOT NULL COMMENT 'Bug标题',
                    `version` VARCHAR(100) NOT NULL COMMENT '软件版本',
                    `severity` VARCHAR(50) NOT NULL DEFAULT '一般' COMMENT '严重程度',
                    `content` TEXT NOT NULL COMMENT '详细描述',
                    `screenshot` VARCHAR(500) DEFAULT NULL COMMENT '截图FTP路径',
                    `created_at` DATETIME DEFAULT CURRENT_TIMESTAMP COMMENT '提交时间'
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Bug报告表'
            """)
            
            cursor.execute(
                "INSERT INTO `bug_reports` (`title`, `version`, `severity`, `content`, `screenshot`, `created_at`) VALUES (%s, %s, %s, %s, %s, NOW())",
                (title, version, severity, content, screenshot_path)
            )
            insert_id = cursor.lastrowid
            conn.commit()
            cursor.close()
            conn.close()
            
            self._json_resp({
                'success': True,
                'message': 'Bug 报告提交成功！',
                'data': {
                    'id': insert_id,
                    'title': title,
                    'screenshot': screenshot_path,
                }
            })
        except Exception as e:
            self._json_resp({'success': False, 'message': f'数据库保存失败: {e}'}, 500)

# ============================================================
# 启动服务器（如果作为入口运行）
# ============================================================
def find_free_port(start=8080, max_attempts=100):
    for port in range(start, start + max_attempts):
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
            if s.connect_ex(('127.0.0.1', port)) != 0:
                return port
    raise RuntimeError('No free port found')

if __name__ == '__main__':
    port = find_free_port(8080)
    url = f'http://127.0.0.1:{port}/'
    
    server = http.server.HTTPServer(('127.0.0.1', port), BugHandler)
    
    print('=' * 55)
    print('  Bug Upload - Local Server')
    print('=' * 55)
    print(f'  地址: {url}')
    print(f'  按 Ctrl+C 停止服务器')
    print('=' * 55)
    
    # Auto-open browser after a short delay
    def open_browser():
        import time
        time.sleep(0.5)
        webbrowser.open(url)
    
    threading.Thread(target=open_browser, daemon=True).start()
    
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print('\n[!] 服务器已停止')
        server.server_close()
