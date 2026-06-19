<?php
// AprVisual perf API — read-only query over /<platform>/data.json. No writes, no secrets.
header('Access-Control-Allow-Origin: *');
$platforms = ['x64', 'arm64'];
$platform  = $_GET['platform'] ?? '';
$opt = JSON_UNESCAPED_UNICODE | JSON_PRETTY_PRINT;

// no platform -> list platforms + latest summaries
if ($platform === '') {
    header('Content-Type: application/json; charset=utf-8');
    $out = ['platforms' => []];
    foreach ($platforms as $p) {
        $f = __DIR__ . "/$p/data.json";
        if (is_file($f)) {
            $d = json_decode(file_get_contents($f), true);
            $last = end($d['versions']);
            $out['platforms'][$p] = [
                'cpu' => $d['cpu'], 'mode' => $d['mode'],
                'versions' => count($d['versions']), 'generated' => $d['generated'],
                'latest' => ['version' => $last['version'], 'hc_s_best3' => $last['metrics']['hc_s_best3']],
            ];
        } else {
            $out['platforms'][$p] = null;   // zone not populated yet
        }
    }
    echo json_encode($out, $opt); exit;
}

if (!in_array($platform, $platforms, true)) { http_response_code(400); echo '{"ok":false,"err":"bad platform"}'; exit; }
$f = __DIR__ . "/$platform/data.json";       // platform whitelisted -> no path traversal
if (!is_file($f)) { http_response_code(404); echo '{"ok":false,"err":"no data for platform yet"}'; exit; }
$d = json_decode(file_get_contents($f), true);

// format=csv
if (($_GET['format'] ?? '') === 'csv') {
    header('Content-Type: text/csv; charset=utf-8');
    echo "version,date,tfm,hc_s_best3,hc_s_median,cyc_per_hc_locked,il_size,native_size,bit_exact,realtime_x\n";
    foreach ($d['versions'] as $v) {
        $m = $v['metrics'];
        echo implode(',', [$v['version'], $v['date'], $v['tfm'], $m['hc_s_best3'], $m['hc_s_median'],
            $m['cyc_per_hc_locked'] ?? '', $m['il_size'] ?? '', $m['native_size'] ?? '',
            $m['bit_exact'] ? 1 : 0, $m['realtime_x'] ?? '']) . "\n";
    }
    exit;
}

header('Content-Type: application/json; charset=utf-8');

if (isset($_GET['version'])) {               // single version
    foreach ($d['versions'] as $v) if ($v['version'] === $_GET['version']) { echo json_encode($v, $opt); exit; }
    http_response_code(404); echo '{"ok":false,"err":"no such version"}'; exit;
}
if (isset($_GET['metric'])) {                // one metric across versions -> [{version,value}]
    $k = $_GET['metric']; $out = [];
    foreach ($d['versions'] as $v) $out[] = ['version' => $v['version'], 'value' => $v['metrics'][$k] ?? null];
    echo json_encode($out, $opt); exit;
}
if (isset($_GET['latest'])) { echo json_encode(end($d['versions']), $opt); exit; }

echo json_encode($d, $opt);                  // default: full document
