# Simula uno o due dita sullo schermo (coordinate fisiche in pixel) tramite InjectTouchInput.
# Uso: .\TouchInject.ps1 -Paths "x1,y1;x2,y2;..." [-Paths2 "..."] [-StepMs 16] [-HoldEndMs 0]
param(
    [Parameter(Mandatory)] [string]$Paths,
    [string]$Paths2 = "",
    [int]$StepMs = 16,
    [int]$HoldEndMs = 0
)

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Touch {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINTER_INFO {
        public int pointerType; public uint pointerId; public uint frameId; public int pointerFlags;
        public IntPtr sourceDevice; public IntPtr hwndTarget; public POINT ptPixelLocation; public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw; public POINT ptHimetricLocationRaw; public uint dwTime; public uint historyCount;
        public int InputData; public uint dwKeyStates; public ulong PerformanceCount; public int ButtonChangeType;
    }
    [StructLayout(LayoutKind.Sequential)] public struct POINTER_TOUCH_INFO {
        public POINTER_INFO pointerInfo; public int touchFlags; public int touchMask; public RECT rcContact; public RECT rcContactRaw;
        public uint orientation; public uint pressure;
    }
    [DllImport("user32.dll", SetLastError = true)] public static extern bool InitializeTouchInjection(uint max, int mode);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool InjectTouchInput(uint count, [In] POINTER_TOUCH_INFO[] contacts);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);

    public const int DOWN = 0x10000 | 0x2 | 0x4, UPDATE = 0x20000 | 0x2 | 0x4, UP = 0x40000;

    public static POINTER_TOUCH_INFO Make(uint id, int x, int y, int flags) {
        var t = new POINTER_TOUCH_INFO();
        t.pointerInfo.pointerType = 2; // PT_TOUCH
        t.pointerInfo.pointerId = id;
        t.pointerInfo.ptPixelLocation.X = x; t.pointerInfo.ptPixelLocation.Y = y;
        t.pointerInfo.pointerFlags = flags;
        t.touchMask = 0x1 | 0x2 | 0x4; // contact area, orientation, pressure
        t.rcContact.Left = x - 4; t.rcContact.Right = x + 4; t.rcContact.Top = y - 4; t.rcContact.Bottom = y + 4;
        t.orientation = 90; t.pressure = 32000;
        return t;
    }
}
"@

[Touch]::SetProcessDpiAwarenessContext([IntPtr]-4) | Out-Null # per-monitor v2: coordinate fisiche
if (-not [Touch]::InitializeTouchInjection(2, 1)) { throw "InitializeTouchInjection non riuscita: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }

function Parse($s) { if (-not $s) { return @() }; $s.Split(';') | ForEach-Object { $a = $_.Split(','); ,@([int]$a[0], [int]$a[1]) } }
$p1 = @(Parse $Paths); $p2 = @(Parse $Paths2)
$n = [Math]::Max($p1.Count, $p2.Count)

function Frame($i, $flags1, $flags2) {
    $list = New-Object System.Collections.Generic.List[Touch+POINTER_TOUCH_INFO]
    $a = $p1[[Math]::Min($i, $p1.Count - 1)]; $list.Add([Touch]::Make(0, $a[0], $a[1], $flags1))
    if ($p2.Count -gt 0) { $b = $p2[[Math]::Min($i, $p2.Count - 1)]; $list.Add([Touch]::Make(1, $b[0], $b[1], $flags2)) }
    if (-not [Touch]::InjectTouchInput($list.Count, $list.ToArray())) { throw "InjectTouchInput non riuscita al passo ${i}: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }
}

Frame 0 ([Touch]::DOWN) ([Touch]::DOWN)
for ($i = 1; $i -lt $n; $i++) { Start-Sleep -Milliseconds $StepMs; Frame $i ([Touch]::UPDATE) ([Touch]::UPDATE) }
$t = 0; while ($t -lt $HoldEndMs) { Start-Sleep -Milliseconds $StepMs; Frame ($n - 1) ([Touch]::UPDATE) ([Touch]::UPDATE); $t += $StepMs }
Start-Sleep -Milliseconds $StepMs
Frame ($n - 1) ([Touch]::UP) ([Touch]::UP)
"ok: $n passi"
