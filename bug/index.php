<?php
/**
 * Bug 报告查看页面
 * 部署在与 MySQL 同一台云服务器上
 * 
 * 首次访问时设置管理员账号密码，所有凭证均 AES-256-CBC 加密存储。
 * 支持图片预览、Bug 处理状态标记。
 */

// ============================================================
// 加密凭证 (AES-256-CBC) - 密文，无密钥无法还原
// ============================================================
$_ENCRYPTED_CONFIG = [
    'DB_HOST' => 'OMPYUtPSUFX3L+BGtYcn43UxtmctvcItUAqeycNTHVM=',
    'DB_PORT' => '9ap6QrxmWTsUBNJIae5X7Cvdvw/4zkhZp4/6gBFMWfw=',
    'DB_NAME' => 'YUXCawdZ06ud+MrWgCpNJ9TGm0Zu/ZqLncJP7Cq8UiyeWU5HWKfmBKb7Bhx15xtv',
    'DB_USER' => 'tsDI3EdHzsYfisFLUFg2x2JT3ylh10FO0lwvwbMdyHB2kGJftS8V73wUZZM32JUn',
    'DB_PASS' => 'kRQ5YTivYPNRaLUQrf3IYIAP7Cur4tcZUMhctAyPInyFVWzZOfJkgggFC5i7pW+x',
];

// ============================================================
// 密钥碎片重建函数
// ============================================================
function _rebuild_key() {
    $kf = [];
    $kf[0] = base64_decode('q5YLWQ==');
    $kf[1] = base64_decode('SoooiQ==');
    $kf[2] = base64_decode('FoMw+w==');
    $kf[3] = base64_decode('NC2rIQ==');
    $kf[4] = base64_decode('jfmjNA==');
    $kf[5] = base64_decode('R6DCLA==');
    $kf[6] = base64_decode('pyX2Ww==');
    $kf[7] = base64_decode('cdj5Qg==');
    ksort($kf);
    return implode('', $kf);
}

// ============================================================
// AES-256-CBC 解密
// ============================================================
function _decrypt($b64) {
    static $key = null;
    if ($key === null) $key = _rebuild_key();
    $raw = base64_decode($b64);
    $iv = substr($raw, 0, 16);
    $ct = substr($raw, 16);
    return openssl_decrypt($ct, 'aes-256-cbc', $key, OPENSSL_RAW_DATA, $iv);
}

// 解密所有配置
$DB_HOST = _decrypt($_ENCRYPTED_CONFIG['DB_HOST']);
$DB_PORT = _decrypt($_ENCRYPTED_CONFIG['DB_PORT']);
$DB_NAME = _decrypt($_ENCRYPTED_CONFIG['DB_NAME']);
$DB_USER = _decrypt($_ENCRYPTED_CONFIG['DB_USER']);
$DB_PASS = _decrypt($_ENCRYPTED_CONFIG['DB_PASS']);

// 图片访问根 URL（通过 HTTPS 直链访问）
define('IMG_BASE_URL', 'https://bug.lahong.top/bugimg/');

// ============================================================
// 管理员凭证存储文件
// ============================================================
define('ADMIN_FILE', __DIR__ . '/.admin_creds.php');

// ============================================================
// 会话管理
// ============================================================
session_start();

// 注销
if (isset($_GET['logout'])) {
    session_destroy();
    header('Location: ' . $_SERVER['PHP_SELF']);
    exit;
}

// ============================================================
// 初次设置管理员账号
// ============================================================
$is_setup = file_exists(ADMIN_FILE);
$step = isset($_GET['step']) ? $_GET['step'] : '';

if (!$is_setup && $step !== 'do_setup') {
    showSetupPage();
    exit;
}

// 处理设置提交
if ($step === 'do_setup' && $_SERVER['REQUEST_METHOD'] === 'POST') {
    $setup_user = trim($_POST['admin_user'] ?? '');
    $setup_pass = $_POST['admin_pass'] ?? '';
    $setup_pass2 = $_POST['admin_pass2'] ?? '';
    
    $errors = [];
    if (strlen($setup_user) < 3) $errors[] = '用户名至少3个字符';
    if (strlen($setup_pass) < 6) $errors[] = '密码至少6个字符';
    if ($setup_pass !== $setup_pass2) $errors[] = '两次密码不一致';
    
    if (empty($errors)) {
        $hash = password_hash($setup_pass, PASSWORD_BCRYPT);
        $content = "<?php\n// Admin credentials - DO NOT EDIT\n" .
                   "\$ADMIN_USER = " . var_export($setup_user, true) . ";\n" .
                   "\$ADMIN_HASH = " . var_export($hash, true) . ";\n";
        file_put_contents(ADMIN_FILE, $content);
        chmod(ADMIN_FILE, 0600);
        
        $_SESSION['logged_in'] = true;
        header('Location: ' . $_SERVER['PHP_SELF']);
        exit;
    } else {
        showSetupPage($errors);
        exit;
    }
}

// ============================================================
// 登录验证
// ============================================================
if (isset($_POST['action']) && $_POST['action'] === 'login') {
    require ADMIN_FILE;
    if (isset($ADMIN_USER) && isset($ADMIN_HASH)) {
        if ($_POST['username'] === $ADMIN_USER && password_verify($_POST['password'], $ADMIN_HASH)) {
            $_SESSION['logged_in'] = true;
            header('Location: ' . $_SERVER['PHP_SELF']);
            exit;
        }
    }
    $login_error = '用户名或密码错误';
}

// ============================================================
// 未登录则显示登录页
// ============================================================
if (empty($_SESSION['logged_in'])) {
    showLoginPage($login_error ?? '');
    exit;
}

// ============================================================
// 数据库连接
// ============================================================
try {
    $pdo = new PDO(
        "mysql:host={$DB_HOST};port={$DB_PORT};dbname={$DB_NAME};charset=utf8mb4",
        $DB_USER,
        $DB_PASS,
        [PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION, PDO::ATTR_DEFAULT_FETCH_MODE => PDO::FETCH_ASSOC]
    );
    
    // 确保 status 列存在
    $pdo->exec("ALTER TABLE `bug_reports` ADD COLUMN IF NOT EXISTS `status` VARCHAR(20) NOT NULL DEFAULT '未处理' COMMENT '处理状态' AFTER `screenshot`");
} catch (Exception $e) {
    // If ALTER fails due to MySQL < 8.0, try without IF NOT EXISTS
    try {
        $pdo->exec("ALTER TABLE `bug_reports` ADD COLUMN `status` VARCHAR(20) NOT NULL DEFAULT '未处理' COMMENT '处理状态' AFTER `screenshot`");
    } catch (Exception $e2) {
        // Column might already exist, ignore
    }
}

// ============================================================
// 状态变更操作
// ============================================================
if (isset($_GET['action']) && isset($_GET['id'])) {
    $action = $_GET['action'];
    $bug_id = intval($_GET['id']);
    
    if ($action === 'resolve') {
        $pdo->prepare("UPDATE bug_reports SET status = '已处理' WHERE id = ?")->execute([$bug_id]);
    } elseif ($action === 'unresolve') {
        $pdo->prepare("UPDATE bug_reports SET status = '未处理' WHERE id = ?")->execute([$bug_id]);
    } elseif ($action === 'delete') {
        // 先查截图路径
        $st = $pdo->prepare("SELECT screenshot FROM bug_reports WHERE id = ?");
        $st->execute([$bug_id]);
        $row = $st->fetch();
        // 删除数据库记录
        $pdo->prepare("DELETE FROM bug_reports WHERE id = ?")->execute([$bug_id]);
        // 删除服务器上的截图文件
        if (!empty($row['screenshot'])) {
            $img_path = $_SERVER['DOCUMENT_ROOT'] . '/bugimg/' . $row['screenshot'];
            if (file_exists($img_path)) {
                @unlink($img_path);
            }
        }
    }
    
    // 重定向回当前列表（保留筛选参数）
    $redirect_params = [];
    if (!empty($_GET['search'])) $redirect_params[] = 'search=' . urlencode($_GET['search']);
    if (!empty($_GET['severity'])) $redirect_params[] = 'severity=' . urlencode($_GET['severity']);
    if (!empty($_GET['status'])) $redirect_params[] = 'status=' . urlencode($_GET['status']);
    if (!empty($_GET['page'])) $redirect_params[] = 'page=' . intval($_GET['page']);
    $redirect = empty($redirect_params) ? '?' : '?' . implode('&', $redirect_params);
    header('Location: ' . $_SERVER['PHP_SELF'] . $redirect);
    exit;
}

// ============================================================
// 分页与筛选参数
// ============================================================
$search = trim($_GET['search'] ?? '');
$severity_filter = $_GET['severity'] ?? '';
$status_filter = $_GET['status'] ?? '';
$page = max(1, intval($_GET['page'] ?? 1));
$per_page = 20;

// ============================================================
// 构建查询
// ============================================================
$where = [];
$params = [];

if ($search !== '') {
    $where[] = '(title LIKE :s1 OR content LIKE :s2 OR version LIKE :s3)';
    $params[':s1'] = "%{$search}%";
    $params[':s2'] = "%{$search}%";
    $params[':s3'] = "%{$search}%";
}
if ($severity_filter !== '') {
    $where[] = 'severity = :sev';
    $params[':sev'] = $severity_filter;
}
if ($status_filter !== '') {
    $where[] = 'status = :st';
    $params[':st'] = $status_filter;
}

$where_clause = empty($where) ? '' : 'WHERE ' . implode(' AND ', $where);

// 总数
$count_stmt = $pdo->prepare("SELECT COUNT(*) FROM bug_reports {$where_clause}");
$count_stmt->execute($params);
$total = $count_stmt->fetchColumn();

// 未处理总数
$unresolved_stmt = $pdo->query("SELECT COUNT(*) FROM bug_reports WHERE status = '未处理'");
$unresolved_count = $unresolved_stmt->fetchColumn();

$total_pages = max(1, ceil($total / $per_page));
$page = min($page, $total_pages);
$offset = ($page - 1) * $per_page;

// 查询数据
$stmt = $pdo->prepare(
    "SELECT * FROM bug_reports {$where_clause} ORDER BY created_at DESC LIMIT {$per_page} OFFSET {$offset}"
);
$stmt->execute($params);
$bugs = $stmt->fetchAll();

// ============================================================
// 输出页面
// ============================================================
showBugListPage($bugs, $total, $unresolved_count, $page, $total_pages, $search, $severity_filter, $status_filter, $db_error ?? '');

// ============================================================
// 页面渲染函数
// ============================================================

function showSetupPage($errors = []) {
    $error_html = '';
    if (!empty($errors)) {
        $error_html = '<div class="msg error"><ul><li>' . implode('</li><li>', array_map('htmlspecialchars', $errors)) . '</li></ul></div>';
    }
    
    echo '<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>初次设置 - Bug 查看系统</title>
<style>
*{margin:0;padding:0;box-sizing:border-box}
body{font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Arial,sans-serif;background:linear-gradient(135deg,#1a1a2e 0%,#16213e 50%,#0f3460 100%);min-height:100vh;display:flex;align-items:center;justify-content:center}
.card{background:#fff;border-radius:16px;padding:40px;width:420px;max-width:95%;box-shadow:0 20px 60px rgba(0,0,0,.3)}
.card h2{text-align:center;color:#1a1a2e;margin-bottom:8px;font-size:24px}
.card .sub{text-align:center;color:#888;margin-bottom:28px;font-size:14px}
.form-group{margin-bottom:20px}
.form-group label{display:block;margin-bottom:6px;color:#333;font-weight:600;font-size:14px}
.form-group input{width:100%;padding:12px 16px;border:2px solid #e0e0e0;border-radius:10px;font-size:15px;transition:border-color .2s;outline:none}
.form-group input:focus{border-color:#4a6cf7}
.btn{width:100%;padding:14px;background:linear-gradient(135deg,#4a6cf7,#6366f1);color:#fff;border:none;border-radius:10px;font-size:16px;font-weight:600;cursor:pointer;transition:transform .1s,box-shadow .2s}
.btn:hover{transform:translateY(-1px);box-shadow:0 8px 25px rgba(74,108,247,.4)}
.msg{padding:12px 16px;border-radius:8px;margin-bottom:20px;font-size:14px}
.msg.error{background:#fff0f0;color:#c53030;border:1px solid #fed7d7}
.msg.error ul{margin-left:20px}
.footer{text-align:center;color:#aaa;margin-top:24px;font-size:12px}
</style>
</head>
<body>
<div class="card">
    <h2>首次设置</h2>
    <p class="sub">请设置管理员账号密码</p>
    ' . $error_html . '
    <form method="POST" action="?step=do_setup">
        <div class="form-group">
            <label>管理员用户名</label>
            <input type="text" name="admin_user" placeholder="至少3个字符" required minlength="3" autofocus>
        </div>
        <div class="form-group">
            <label>管理员密码</label>
            <input type="password" name="admin_pass" placeholder="至少6个字符" required minlength="6">
        </div>
        <div class="form-group">
            <label>确认密码</label>
            <input type="password" name="admin_pass2" placeholder="再次输入密码" required minlength="6">
        </div>
        <button type="submit" class="btn">完成设置</button>
    </form>
    <p class="footer">凭证将使用 bcrypt 加密存储到服务器</p>
</div>
</body>
</html>';
}

function showLoginPage($error = '') {
    $error_html = $error ? '<div class="msg error">' . htmlspecialchars($error) . '</div>' : '';
    
    echo '<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>登录 - Bug 查看系统</title>
<style>
*{margin:0;padding:0;box-sizing:border-box}
body{font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Arial,sans-serif;background:linear-gradient(135deg,#1a1a2e 0%,#16213e 50%,#0f3460 100%);min-height:100vh;display:flex;align-items:center;justify-content:center}
.card{background:#fff;border-radius:16px;padding:40px;width:400px;max-width:95%;box-shadow:0 20px 60px rgba(0,0,0,.3)}
.card h2{text-align:center;color:#1a1a2e;margin-bottom:4px;font-size:24px}
.card .sub{text-align:center;color:#888;margin-bottom:28px;font-size:14px}
.form-group{margin-bottom:20px}
.form-group label{display:block;margin-bottom:6px;color:#333;font-weight:600;font-size:14px}
.form-group input{width:100%;padding:12px 16px;border:2px solid #e0e0e0;border-radius:10px;font-size:15px;transition:border-color .2s;outline:none}
.form-group input:focus{border-color:#4a6cf7}
.btn{width:100%;padding:14px;background:linear-gradient(135deg,#4a6cf7,#6366f1);color:#fff;border:none;border-radius:10px;font-size:16px;font-weight:600;cursor:pointer;transition:transform .1s,box-shadow .2s}
.btn:hover{transform:translateY(-1px);box-shadow:0 8px 25px rgba(74,108,247,.4)}
.msg{padding:12px 16px;border-radius:8px;margin-bottom:20px;font-size:14px}
.msg.error{background:#fff0f0;color:#c53030;border:1px solid #fed7d7}
</style>
</head>
<body>
<div class="card">
    <h2>Bug 查看系统</h2>
    <p class="sub">请输入管理员密码</p>
    ' . $error_html . '
    <form method="POST">
        <input type="hidden" name="action" value="login">
        <div class="form-group">
            <label>用户名</label>
            <input type="text" name="username" placeholder="管理员用户名" required autofocus>
        </div>
        <div class="form-group">
            <label>密码</label>
            <input type="password" name="password" placeholder="管理员密码" required>
        </div>
        <button type="submit" class="btn">登 录</button>
    </form>
</div>
</body>
</html>';
}

function showBugListPage($bugs, $total, $unresolved_count, $page, $total_pages, $search, $severity_filter, $status_filter, $db_error = '') {
    $severity_colors = [
        '致命' => '#dc2626',
        '严重' => '#ea580c',
        '一般' => '#ca8a04',
        '轻微' => '#16a34a',
    ];
    
    // 构建统一查询参数
    $base_params = [];
    if ($search !== '') $base_params[] = 'search=' . urlencode($search);
    if ($severity_filter !== '') $base_params[] = 'severity=' . urlencode($severity_filter);
    if ($status_filter !== '') $base_params[] = 'status=' . urlencode($status_filter);
    $base_query = empty($base_params) ? '' : '&' . implode('&', $base_params);
    
    $pagination = '';
    if ($total_pages > 1) {
        $pagination = '<div class="pagination">';
        if ($page > 1) {
            $pagination .= '<a href="?page=' . ($page - 1) . $base_query . '" class="page-btn">&laquo; 上一页</a>';
        }
        $pagination .= '<span class="page-info">第 ' . $page . ' / ' . $total_pages . ' 页（共 ' . $total . ' 条）</span>';
        if ($page < $total_pages) {
            $pagination .= '<a href="?page=' . ($page + 1) . $base_query . '" class="page-btn">下一页 &raquo;</a>';
        }
        $pagination .= '</div>';
    }
    
    $table_rows = '';
    if (!empty($bugs)) {
        foreach ($bugs as $bug) {
            $sev = htmlspecialchars($bug['severity']);
            $color = $severity_colors[$sev] ?? '#6b7280';
            $sev_badge = '<span class="severity" style="background:' . $color . '">' . $sev . '</span>';
            
            // 图片预览
            $image_cell = '<span class="no-img">-</span>';
            if (!empty($bug['screenshot'])) {
                $img_url = IMG_BASE_URL . $bug['screenshot'];
                $escaped_url = htmlspecialchars($img_url);
                $image_cell = '<img src="' . $escaped_url . '" class="thumb" alt="截图" loading="lazy" onclick="openPreview(\'' . $escaped_url . '\')" onerror="this.outerHTML=\'<span class=no-img>加载失败</span>\'">';
            }
            
            // 处理状态
            $status = $bug['status'] ?? '未处理';
            $status_class = ($status === '已处理') ? 'status-done' : 'status-pending';
            $status_badge = '<span class="status-badge ' . $status_class . '">' . htmlspecialchars($status) . '</span>';
            
            // 操作按钮
            $bug_id = $bug['id'];
            if ($status === '未处理') {
                $action_btn = '<a href="?action=resolve&id=' . $bug_id . $base_query . '" class="act-btn act-done" onclick="return confirm(\'确认将此 Bug 标记为已处理？\')">标记已处理</a>';
            } else {
                $action_btn = '<a href="?action=unresolve&id=' . $bug_id . $base_query . '" class="act-btn act-reset" onclick="return confirm(\'确认将此 Bug 重置为未处理？\')">重置未处理</a>';
                $action_btn .= ' <a href="?action=delete&id=' . $bug_id . $base_query . '" class="act-btn act-delete" onclick="return confirm(\'确认删除此 Bug 及截图？不可撤销！\')">删除</a>';
            }
            
            $content_preview = mb_strlen($bug['content']) > 200 
                ? htmlspecialchars(mb_substr($bug['content'], 0, 200)) . '...' 
                : htmlspecialchars($bug['content']);
            
            $screenshot_info = '';
            if (!empty($bug['screenshot'])) {
                $screenshot_info = '<br><small class="img-path">' . htmlspecialchars($bug['screenshot']) . '</small>';
            }
            
            $row_class = ($status === '已处理') ? ' class="row-done"' : '';
            $table_rows .= '
            <tr' . $row_class . '>
                <td>' . htmlspecialchars($bug_id) . '</td>
                <td class="title-cell"><strong>' . htmlspecialchars($bug['title']) . '</strong></td>
                <td>' . htmlspecialchars($bug['version']) . '</td>
                <td>' . $sev_badge . '</td>
                <td class="img-cell">' . $image_cell . '</td>
                <td class="content-cell">' . nl2br($content_preview) . $screenshot_info . '</td>
                <td>' . htmlspecialchars($bug['created_at']) . '</td>
                <td>' . $status_badge . '</td>
                <td class="action-cell">' . $action_btn . '</td>
            </tr>';
        }
    } else {
        $table_rows = '<tr><td colspan="9" class="empty">暂无 Bug 报告</td></tr>';
    }
    
    $search_val = htmlspecialchars($search);
    
    // 严重性下拉
    $severity_options = ['致命','严重','一般','轻微'];
    $sev_select = '<option value="">全部严重性</option>';
    foreach ($severity_options as $s) {
        $sel = ($severity_filter === $s) ? ' selected' : '';
        $sev_select .= '<option value="' . $s . '"' . $sel . '>' . $s . '</option>';
    }
    
    // 状态下拉
    $status_options = ['未处理','已处理'];
    $st_select = '<option value="">全部状态</option>';
    foreach ($status_options as $s) {
        $sel = ($status_filter === $s) ? ' selected' : '';
        $st_select .= '<option value="' . $s . '"' . $sel . '>' . $s . '</option>';
    }
    
    $error_html = $db_error ? '<div class="msg error">' . htmlspecialchars($db_error) . '</div>' : '';
    
    echo '<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>Bug 报告列表</title>
<style>
*{margin:0;padding:0;box-sizing:border-box}
body{font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Arial,sans-serif;background:#f5f7fb;min-height:100vh}
.header{background:linear-gradient(135deg,#1a1a2e 0%,#16213e 50%,#0f3460 100%);padding:20px 0;box-shadow:0 2px 12px rgba(0,0,0,.15)}
.header-inner{max-width:1500px;margin:0 auto;padding:0 24px;display:flex;justify-content:space-between;align-items:center}
.header h1{color:#fff;font-size:22px;font-weight:700}
.logout-btn{color:#fff;text-decoration:none;padding:8px 20px;background:rgba(255,255,255,.15);border-radius:8px;font-size:14px;transition:background .2s}
.logout-btn:hover{background:rgba(255,255,255,.25)}
.container{max-width:1500px;margin:0 auto;padding:24px}
.stats-bar{display:flex;gap:16px;margin-bottom:24px;flex-wrap:wrap}
.stat-card{background:#fff;padding:20px 28px;border-radius:12px;box-shadow:0 1px 4px rgba(0,0,0,.06);flex:1;min-width:140px}
.stat-card .num{font-size:28px;font-weight:700;color:#1a1a2e}
.stat-card .label{font-size:13px;color:#888;margin-top:4px}
.stat-card.unresolved .num{color:#dc2626}
.toolbar{background:#fff;border-radius:12px;padding:20px 24px;margin-bottom:20px;box-shadow:0 1px 4px rgba(0,0,0,.06);display:flex;gap:12px;align-items:center;flex-wrap:wrap}
.toolbar input,.toolbar select{padding:10px 14px;border:2px solid #e0e0e0;border-radius:8px;font-size:14px;outline:none;transition:border-color .2s}
.toolbar input{flex:1;min-width:180px}
.toolbar input:focus,.toolbar select:focus{border-color:#4a6cf7}
.toolbar button{padding:10px 20px;background:#4a6cf7;color:#fff;border:none;border-radius:8px;font-size:14px;font-weight:600;cursor:pointer;transition:background .2s}
.toolbar button:hover{background:#3b5de7}
.toolbar .reset-btn{background:#e5e7eb;color:#333;text-decoration:none;padding:10px 20px;border-radius:8px;font-size:14px;display:inline-block}
.toolbar .reset-btn:hover{background:#d1d5db}
.table-wrap{background:#fff;border-radius:12px;box-shadow:0 1px 4px rgba(0,0,0,.06);overflow-x:auto}
table{width:100%;border-collapse:collapse;min-width:1200px}
th{background:#f8fafc;padding:14px 12px;text-align:left;font-weight:600;color:#475569;font-size:12px;text-transform:uppercase;letter-spacing:.3px;border-bottom:2px solid #e2e8f0;white-space:nowrap}
td{padding:12px;border-bottom:1px solid #f1f5f9;font-size:13px;color:#334155;vertical-align:middle}
tr:hover td{background:#f8fafc}
tr.row-done td{opacity:.35;text-decoration:line-through}
tr.row-done td img{opacity:.35}
tr.row-done:hover td{opacity:.75;background:#f0fdf4}
tr.row-done:hover td img{opacity:.75}
.severity{display:inline-block;padding:3px 10px;border-radius:20px;color:#fff;font-size:11px;font-weight:600}
.title-cell{max-width:200px}
.content-cell{max-width:300px;font-size:12px;line-height:1.5;color:#64748b}
.img-cell{width:110px;text-align:center}
.thumb{width:90px;height:68px;object-fit:cover;border-radius:6px;border:2px solid #e5e7eb;cursor:pointer;transition:border-color .15s}
.thumb:hover{border-color:#4a6cf7}
.no-img{color:#cbd5e1;font-size:12px}
/* ====== 图片预览弹窗 ====== */
.lightbox{display:none;position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,.88);z-index:9999;align-items:center;justify-content:center;cursor:grab}
.lightbox.active{display:flex}
.lightbox.dragging{cursor:grabbing}
.lightbox img{max-width:90vw;max-height:90vh;object-fit:contain;user-select:none;transition:transform .05s linear}
.lightbox .close{position:fixed;top:20px;right:24px;color:#fff;font-size:32px;cursor:pointer;z-index:10000;width:44px;height:44px;display:flex;align-items:center;justify-content:center;border-radius:50%;background:rgba(255,255,255,.1);transition:background .2s;line-height:1}
.lightbox .close:hover{background:rgba(255,255,255,.25)}
.lightbox .hint{position:fixed;bottom:32px;left:50%;transform:translateX(-50%);color:rgba(255,255,255,.45);font-size:13px;z-index:10000;pointer-events:none;text-align:center;letter-spacing:.5px}
.img-path{color:#94a3b8}
.status-badge{display:inline-block;padding:3px 10px;border-radius:20px;font-size:11px;font-weight:600;white-space:nowrap}
.status-pending{background:#fef3c7;color:#92400e}
.status-done{background:#d1fae5;color:#065f46}
.action-cell{white-space:nowrap}
.act-btn{display:inline-block;padding:4px 12px;border-radius:6px;font-size:11px;font-weight:600;text-decoration:none;cursor:pointer;transition:all .15s}
.act-done{background:#10b981;color:#fff}
.act-done:hover{background:#059669}
.act-reset{background:#f59e0b;color:#fff}
.act-reset:hover{background:#d97706}
.act-delete{background:#ef4444;color:#fff}
.act-delete:hover{background:#dc2626}
.empty{text-align:center;padding:60px 20px;color:#94a3b8;font-size:15px}
.pagination{display:flex;justify-content:center;align-items:center;gap:16px;margin-top:24px;padding:16px}
.page-btn{text-decoration:none;padding:10px 20px;background:#fff;border:2px solid #e0e0e0;border-radius:8px;color:#4a6cf7;font-weight:600;transition:all .2s}
.page-btn:hover{background:#4a6cf7;color:#fff;border-color:#4a6cf7}
.page-info{color:#888;font-size:14px}
.msg{padding:12px 16px;border-radius:8px;margin-bottom:20px;font-size:14px}
.msg.error{background:#fff0f0;color:#c53030;border:1px solid #fed7d7}
.msg.success{background:#f0fff4;color:#276749;border:1px solid #c6f6d5}
</style>
</head>
<body>
<div class="header">
    <div class="header-inner">
        <h1>Bug 报告管理系统</h1>
        <a href="?logout=1" class="logout-btn">退出登录</a>
    </div>
</div>

<div class="container">
    ' . $error_html . '
    
    <div class="stats-bar">
        <div class="stat-card">
            <div class="num">' . $total . '</div>
            <div class="label">Bug 总数</div>
        </div>
        <div class="stat-card unresolved">
            <div class="num">' . $unresolved_count . '</div>
            <div class="label">待处理</div>
        </div>
    </div>
    
    <form class="toolbar" method="GET">
        <input type="text" name="search" placeholder="搜索标题、版本或描述内容..." value="' . $search_val . '">
        <select name="severity">
            ' . $sev_select . '
        </select>
        <select name="status">
            ' . $st_select . '
        </select>
        <button type="submit">搜索</button>
        <a href="?" class="reset-btn">重置</a>
    </form>
    
    ' . $pagination . '
    
    <div class="table-wrap">
        <table>
            <thead>
                <tr>
                    <th style="width:50px">ID</th>
                    <th style="width:200px">标题</th>
                    <th style="width:90px">版本</th>
                    <th style="width:70px">严重性</th>
                    <th style="width:110px">截图预览</th>
                    <th>描述</th>
                    <th style="width:140px">提交时间</th>
                    <th style="width:80px">状态</th>
                    <th style="width:150px">操作</th>
                </tr>
            </thead>
            <tbody>
                ' . $table_rows . '
            </tbody>
        </table>
    </div>
    
    ' . $pagination . '
</div>

<!-- 图片预览弹窗 -->
<div class="lightbox" id="lightbox" onclick="closePreview()">
    <span class="close" onclick="event.stopPropagation();closePreview()">&times;</span>
    <img id="lightboxImg" src="" alt="预览" ondragstart="return false">
    <div class="hint">滚轮缩放 &middot; 拖拽移动 &middot; ESC 关闭</div>
</div>

<script>
(function(){
    var lb = document.getElementById("lightbox");
    var img = document.getElementById("lightboxImg");
    var scale = 1;
    var posX = 0, posY = 0;
    var dragging = false, startX, startY, initX, initY;

    function updateTransform() {
        img.style.transform = "translate(" + posX + "px, " + posY + "px) scale(" + scale + ")";
    }

    window.openPreview = function(url) {
        img.src = url;
        scale = 1;
        posX = 0;
        posY = 0;
        updateTransform();
        lb.classList.add("active");
        document.body.style.overflow = "hidden";
    };

    window.closePreview = function() {
        lb.classList.remove("active");
        img.src = "";
        document.body.style.overflow = "";
    };

    // 鼠标滚轮缩放（绑定到遮罩，覆盖整个区域）
    lb.addEventListener("wheel", function(e) {
        e.preventDefault();
        var delta = e.deltaY > 0 ? -0.15 : 0.15;
        scale = Math.min(6, Math.max(0.3, scale + delta));
        updateTransform();
    }, {passive: false});

    // 鼠标按下开始拖拽
    img.addEventListener("mousedown", function(e) {
        dragging = true;
        startX = e.clientX;
        startY = e.clientY;
        initX = posX;
        initY = posY;
        lb.classList.add("dragging");
        e.preventDefault();
    });

    // 鼠标移动
    window.addEventListener("mousemove", function(e) {
        if (!dragging) return;
        posX = initX + (e.clientX - startX);
        posY = initY + (e.clientY - startY);
        updateTransform();
    });

    // 鼠标松开
    window.addEventListener("mouseup", function() {
        dragging = false;
        lb.classList.remove("dragging");
    });

    // ESC 关闭
    window.addEventListener("keydown", function(e) {
        if (e.key === "Escape" && lb.classList.contains("active")) {
            closePreview();
        }
    });

    // 阻止图片区域点击关闭
    img.addEventListener("click", function(e) {
        e.stopPropagation();
    });
})();
</script>

</body>
</html>';
}
