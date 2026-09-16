# Reverse Engineering Lenovo Keyboard Backlight Control

This document explains the step-by-step reverse engineering process that led to direct hardware control of the keyboard backlight on Lenovo laptops (IdeaPad, Legion, Yoga, Slim) without requiring Lenovo Vantage or Administrator privileges.

---

## 1. Problem Statement

On Lenovo IdeaPad / Legion laptops running Windows 10/11 with Modern Standby (S0ix) or ACPI S3 sleep:
- When the laptop enters sleep, the Embedded Controller (EC) turns off the keyboard backlight to save power.
- Upon waking up, the firmware does not automatically restore the previous backlight level.
- The user is forced to manually press `Fn + Space` every single time they wake or open the laptop.
- Existing generic tools (e.g. keyboard simulation, virtual keys) fail because `Fn + Space` is intercepted directly at the hardware/EC level and is not a standard Windows virtual key.

However, **Lenovo Vantage** has an "Input" section in its GUI that can toggle between **Off**, **Low**, and **High**. This proved that a software control path exists.

---

## 2. Investigating Lenovo Vantage Architecture

Lenovo Vantage is split into:
1. **Frontend UWP App:** `E046963F.LenovoCompanion_*` in `C:\Program Files\WindowsApps\`
2. **Backend Service:** `LenovoVantageService` in `C:\Program Files (x86)\Lenovo\VantageService\`
3. **Addins / Plugins:** Located in `C:\ProgramData\Lenovo\Vantage\Addins\`

Examining `C:\ProgramData\Lenovo\Vantage\Addins\`, we found:
- `IdeaKBDManagerAddin`
- `IdeaNotebookAddin` (contains `KeyboardContract.dll`, `IdeaNotebookAddin.dll`, `IdeaPowerAgent.dll`)

---

## 3. Disassembling `IdeaNotebookAddin.dll`

Using .NET reflection and Intermediate Language (IL) disassembly on `IdeaNotebookAddin.dll`, we located the keyboard handling logic:

```text
Namespace: IdeaNotebookAddin
Class:     IdeaNotebookAddin.IdeaNotebookAgent
Methods:   GetKblLevels(), GetKblOutput(uint), SetBacklightStatus(...)
```

### Driver Discovery
In `IdeaNotebookAgent`'s constructor and helper methods:
```csharp
IdeaNotebookAgent.gAcpiVpcDriverName = @"\\.\EnergyDrv";
```
The driver backing `\\.\EnergyDrv` is `AcpiVpc.sys` (Lenovo ACPI Virtual Power Controller driver, part of Lenovo Energy Management).

### ACLs & Security
Crucially, `\\.\EnergyDrv` is created with permissions that allow **standard users** (medium integrity) to open it with `GENERIC_READ | GENERIC_WRITE` and issue IOCTL requests. **No administrator elevation (UAC) is needed!**

---

## 4. Dissecting the Protocol

Disassembling `IdeaNotebookAgent.SetBacklightStatus` and `GetKblOutput`:

### Method: `GetKblOutput(uint cmd)`
```csharp
[DllImport("kernel32.dll", SetLastError = true)]
static extern bool DeviceIoControl(
    IntPtr hDevice,
    uint dwIoControlCode,     // 0x83102144 (IOCTL_KBLC)
    ref uint lpInBuffer,      // cmd
    uint nInBufferSize,       // 4 bytes
    ref uint lpOutBuffer,     // outVal
    uint nOutBufferSize,      // 4 bytes
    out uint lpBytesReturned,
    IntPtr lpOverlapped);
```

### IOCTL Code
- **IOCTL_KBLC:** `0x83102144`

### Command Format (Input `cmd` parameter)
The input is a 32-bit unsigned integer formatted as:

$$\text{Command} = (\text{Level} \ll 16) \mid \text{SubFunction}$$

Where:
- **SubFunction `0x23`** = Set Keyboard Backlight Level
- **SubFunction `0x22`** = Query Current Keyboard Backlight Status
- **SubFunction `0x01`** = Query Supported Levels (Capability)

#### Commands Table

| Operation | Input (`uint`) | Hex Value | Return Value (`outVal`) |
|---|---|---|---|
| **Turn OFF** | `(0 << 16) \| 0x23` | `0x00000023` (35) | `(Level << 1) \| 1` = `0x00000001` |
| **Set LOW** | `(1 << 16) \| 0x23` | `0x00010023` (65571) | `(Level << 1) \| 1` = `0x00000003` |
| **Set HIGH** | `(2 << 16) \| 0x23` | `0x00020023` (131107) | `(Level << 1) \| 1` = `0x00000005` |
| **Query Status** | `0x00000022` | `0x00000022` (34) | `(Level << 1) \| 1` (Bit 0 = success, Bits 1-15 = Level) |
| **Query Capability** | `0x00000001` | `0x00000001` (1) | `(MaxLevels << 1) \| 1` (Returns 2 for Low/High) |

### Return Value Decoding
For query (`0x22`) or set (`0x23`):
```csharp
bool isSuccess = (outVal & 1) == 1;
int currentLevel = (int)((outVal >> 1) & 0x7FFF);
```
- `currentLevel == 0` -> Off
- `currentLevel == 1` -> Low
- `currentLevel == 2` -> High

---

## 5. Detecting Wake Events on Modern Standby (S0ix)

On Windows 10/11, Modern Standby does not trigger classic ACPI S3 wake hooks reliably. Instead, wake transitions are signaled through multiple layers:

1. **Display State Power Setting Notification:**
   - `RegisterPowerSettingNotification` with `GUID_CONSOLE_DISPLAY_STATE`:
     - `0x0` = Display Off
     - `0x1` = Display On
     - `0x2` = Display Dimmed
   - When the display turns on (`0x1`), the user is waking the laptop.

2. **Power Broadcast Message (`WM_POWERBROADCAST`):**
   - `PBT_APMRESUMEAUTOMATIC` (`0x0012`)
   - `PBT_APMRESUMESUSPEND` (`0x0007`)

3. **Session Unlock (`WM_WTSSESSION_CHANGE`):**
   - `WTS_SESSION_UNLOCK` (`0x8`)
   - `WTS_SESSION_LOGON` (`0x5`)

4. **Kernel-Power Event Log (Fallback):**
   - Event ID **507**: *"The system is exiting connected standby"*
   - Event ID **107**: *"The system has resumed from sleep"*

### Hardware Resume Timing & Retry
Immediately upon wake, the Embedded Controller (EC) may still be executing its internal low-power exit sequence. If the command is sent too quickly (within the first 50ms), the EC may acknowledge the IOCTL but immediately reset the backlight state when its display power rail stabilizes.

To ensure 100% reliability, `LenovoBacklight` applies the backlight in a fast phased sequence:
1. **0 ms:** Immediate apply
2. **400 ms:** Second apply (handles late EC initialization)
3. **1000 ms:** Final verification check
