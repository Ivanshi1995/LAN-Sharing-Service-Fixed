<?php
/**
 * Bug Report API
 * 所有敏感凭证均使用 AES-256-CBC 加密存储，解密密钥通过环境变量 APP_SECRET_KEY 传入。
 * 源码中不包含任何明文凭证。
 */

header('Content-Type: application/json; charset=utf-8');
header('Access-Control-Allow-Origin: *');
header('Access-Control-Allow-Methods: POST, OPTIONS');
header('Access-Control-Allow-Headers: Content-Type');

if ($_SERVER['REQUEST_METHOD'] === 'OPTIONS') {
    http_response_code(200);
    exit;
}

if ($_SERVER['REQUEST_METHOD'] !== 'POST') {
    http_response_code(405);
    echo json_encode(['success' => false, 'message' => 'Method Not Allowed'], JSON_UNESCAPED_UNICODE);
    exit;
}

// ============================================================
// 加密凭证存储 - 这些密文仅在配合正确 APP_SECRET_KEY 时才能解密
// ============================================================
define('ENCRYPTED_CONFIG', [
    'DB_HOST' => '+EG2wVQY+x1Hhu8EotPRz2zlvUeASstglKegoo/eDjA=',
    'DB_PORT' => 'a4+hQaBOvLR3/xD/UZutopJfA/Tq/TmMJjTWNtsZSsk=',
    'DB_NAME' => 'TsYtJQjEI9cI3LZFwWSHd0wLUDl2//5wOuB0vHiHe906czs2MIBpUBU6UdpOUVh+',
    'DB_USER' => 'X2LToYwwSufdbB31V5h7cKh1aEFKjkZiCwYF/5n7jtIyOPlAHA2t5/99t3PyEBk+',
    'DB_PASS' => 'gnGmjFukGrlxDUoh/78m5IMupLQL1qk20+qtIDJRv6GsLndGqBt5jOYEmQKfCoZF',
    'FTP_HOST' => 'knr2UcW9Yx35V65rv84lB/xHcDlX4QqDfwVaJnuwctY=',
    'FTP_PORT' => 'kA+NLE1KsaTFiH4uLT5/z5ZUxpjAsBQQq0vO9E7T9VQ=',
    'FTP_USER' => 'YrgVIJQQV+mdeypxIdDqPTazHOzxrrZOpJV084GK0lQ=',
    'FTP_PASS' => '4pWRzeyEvrBXeUYBJiMHxPZPQRJmtkbNuP66zefcVUk=',
]);

// ============================================================
// AES-256-CBC 解密函数
// ============================================================
function aes_decrypt(string $encrypted_base64, string $key_hex): string {
    $key = hex2bin($key_hex);
    $data = base64_decode($encrypted_base64);
    if (strlen($data) < 16) {
        throw new RuntimeException('Invalid encrypted data');
    }
    $iv = substr($data, 0, 16);
    $ciphertext = substr($data, 16);
    $decrypted = openssl_decrypt($ciphertext, 'AES-256-CBC', $key, OPENSSL_RAW_DATA, $iv);
    if ($decrypted === false) {
        throw new RuntimeException('Decryption failed - invalid key or corrupted data');
    }
    return $decrypted;
}

// ============================================================
// 从环境变量获取密钥并解密所有配置
// ============================================================
$app_key = getenv('APP_SECRET_KEY');
if (!$app_key || strlen($app_key) !== 64) {
    // 也尝试从 $_ENV 和 $_SERVER 读取
    $app_key = $_ENV['APP_SECRET_KEY'] ?? $_SERVER['APP_SECRET_KEY'] ?? '';
}
if (!$app_key || strlen($app_key) !== 64) {
    http_response_code(500);
    echo json_encode(['success' => false, 'message' => 'Server configuration error: APP_SECRET_KEY not set'], JSON_UNESCAPED_UNICODE);
    exit;
}

try {
    $config = [];
    foreach (ENCRYPTED_CONFIG as $k => $enc) {
        $config[$k] = aes_decrypt($enc, $app_key);
    }
} catch (Exception $e) {
    http_response_code(500);
    echo json_encode(['success' => false, 'message' => 'Server configuration error: ' . $e->getMessage()], JSON_UNESCAPED_UNICODE);
    exit;
}

// ============================================================
// 数据库连接
// ============================================================
try {
    $dsn = "mysql:host={$config['DB_HOST']};port={$config['DB_PORT']};dbname={$config['DB_NAME']};charset=utf8mb4";
    $pdo = new PDO($dsn, $config['DB_USER'], $config['DB_PASS'], [
        PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION,
        PDO::ATTR_DEFAULT_FETCH_MODE => PDO::FETCH_ASSOC,
        PDO::ATTR_EMULATE_PREPARES => false,
    ]);
} catch (PDOException $e) {
    http_response_code(500);
    echo json_encode(['success' => false, 'message' => 'Database connection failed'], JSON_UNESCAPED_UNICODE);
    exit;
}

// 自动建表
try {
    $pdo->exec("CREATE TABLE IF NOT EXISTS `bug_reports` (
        `id` INT AUTO_INCREMENT PRIMARY KEY,
        `title` VARCHAR(500) NOT NULL COMMENT 'Bug标题',
        `version` VARCHAR(100) NOT NULL COMMENT '软件版本',
        `severity` VARCHAR(50) NOT NULL DEFAULT '一般' COMMENT '严重程度',
        `content` TEXT NOT NULL COMMENT '详细描述',
        `screenshot` VARCHAR(500) DEFAULT NULL COMMENT '截图FTP路径',
        `created_at` DATETIME DEFAULT CURRENT_TIMESTAMP COMMENT '提交时间'
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Bug报告表';");
} catch (PDOException $e) {
    // 表可能已存在，忽略
}

// ============================================================
// FTP 上传函数（被动模式）
// ============================================================
function ftp_upload_file(string $host, int $port, string $user, string $pass, string $local_path, string $remote_name): string {
    $conn = ftp_connect($host, $port, 30);
    if (!$conn) {
        throw new RuntimeException('FTP connection failed');
    }
    if (!ftp_login($conn, $user, $pass)) {
        ftp_close($conn);
        throw new RuntimeException('FTP login failed');
    }
    ftp_pasv($conn, true);
    
    // 确保 uploads 目录存在
    @ftp_mkdir($conn, 'bug_screenshots');
    
    $remote_path = 'bug_screenshots/' . $remote_name;
    if (!ftp_put($conn, $remote_path, $local_path, FTP_BINARY)) {
        ftp_close($conn);
        throw new RuntimeException('FTP upload failed');
    }
    ftp_close($conn);
    return $remote_path;
}

// ============================================================
// 处理表单提交
// ============================================================
$title = trim($_POST['title'] ?? '');
$version = trim($_POST['version'] ?? '');
$severity = trim($_POST['severity'] ?? '一般');
$content = trim($_POST['content'] ?? '');

// 基本校验
if ($title === '' || $version === '' || $content === '') {
    http_response_code(400);
    echo json_encode(['success' => false, 'message' => 'Please fill in all required fields (title, version, content)'], JSON_UNESCAPED_UNICODE);
    exit;
}

// 长度限制
if (mb_strlen($title) > 500) {
    http_response_code(400);
    echo json_encode(['success' => false, 'message' => 'Title too long (max 500 characters)'], JSON_UNESCAPED_UNICODE);
    exit;
}
if (mb_strlen($version) > 100) {
    http_response_code(400);
    echo json_encode(['success' => false, 'message' => 'Version too long (max 100 characters)'], JSON_UNESCAPED_UNICODE);
    exit;
}

$screenshot_path = null;

// 处理图片上传到 FTP
if (isset($_FILES['screenshot']) && $_FILES['screenshot']['error'] === UPLOAD_ERR_OK) {
    $file = $_FILES['screenshot'];
    
    // 校验文件类型
    $allowed_types = ['image/png', 'image/jpeg', 'image/gif', 'image/webp'];
    $finfo = finfo_open(FILEINFO_MIME_TYPE);
    $mime = finfo_file($finfo, $file['tmp_name']);
    finfo_close($finfo);
    
    if (!in_array($mime, $allowed_types)) {
        http_response_code(400);
        echo json_encode(['success' => false, 'message' => 'Invalid file type. Only PNG, JPG, GIF, WebP allowed.'], JSON_UNESCAPED_UNICODE);
        exit;
    }
    
    // 文件大小限制 10MB
    if ($file['size'] > 10 * 1024 * 1024) {
        http_response_code(400);
        echo json_encode(['success' => false, 'message' => 'File too large (max 10MB)'], JSON_UNESCAPED_UNICODE);
        exit;
    }
    
    $ext = pathinfo($file['name'], PATHINFO_EXTENSION) ?: 'png';
    $remote_name = date('Ymd_His') . '_' . bin2hex(random_bytes(4)) . '.' . $ext;
    
    try {
        $screenshot_path = ftp_upload_file(
            $config['FTP_HOST'],
            (int)$config['FTP_PORT'],
            $config['FTP_USER'],
            $config['FTP_PASS'],
            $file['tmp_name'],
            $remote_name
        );
    } catch (Exception $e) {
        http_response_code(500);
        echo json_encode(['success' => false, 'message' => 'Screenshot upload failed: ' . $e->getMessage()], JSON_UNESCAPED_UNICODE);
        exit;
    }
}

// 插入数据库
try {
    $stmt = $pdo->prepare("INSERT INTO `bug_reports` (`title`, `version`, `severity`, `content`, `screenshot`, `created_at`) VALUES (?, ?, ?, ?, ?, NOW())");
    $stmt->execute([$title, $version, $severity, $content, $screenshot_path]);
    $insert_id = $pdo->lastInsertId();
    
    echo json_encode([
        'success' => true,
        'message' => 'Bug report submitted successfully',
        'data' => [
            'id' => (int)$insert_id,
            'title' => $title,
            'screenshot' => $screenshot_path,
        ]
    ], JSON_UNESCAPED_UNICODE);
} catch (PDOException $e) {
    http_response_code(500);
    echo json_encode(['success' => false, 'message' => 'Failed to save report'], JSON_UNESCAPED_UNICODE);
    exit;
}
