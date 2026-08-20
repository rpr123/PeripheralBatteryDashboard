# 장치 교체·추가 가이드

> [!IMPORTANT]
> 이 문서는 로컬 Codex 또는 코딩 에이전트가 읽는 구현 참고 자료입니다. 사용자가 프로필·HID 명령·플러그인 DLL을 직접 작성하거나 설치하는 절차가 아닙니다. 먼저 [CODEX-PROMPTS.md](CODEX-PROMPTS.md)의 목적별 프롬프트를 에이전트에 전달하세요.

이 앱은 두 층으로 장치를 지원합니다.

- **장치 프로필(JSON):** VID/PID, HID 인터페이스, Usage Page/Usage처럼 어떤 장치를 찾을지 지정합니다.
- **배터리 공급자(DLL의 `IBatteryProvider`):** 해당 장치에 어떤 요청을 보내고 응답에서 잔량을 어떻게 해석할지 구현합니다.

같은 프로토콜을 쓰는 기기로 바뀌었다면 JSON만 추가하거나 덮어쓰면 됩니다. 명령과 응답 형식이 다른 기기는 새 공급자 플러그인이 필요합니다. 외형이 비슷하거나 제조사가 같다는 이유만으로 같은 프로토콜이라고 가정하면 안 됩니다.

## 1. 기존 공급자를 쓰는 장치로 교체

먼저 Windows 장치 관리자, 진단 내보내기 또는 제조사 문서에서 다음 값을 확인합니다.

- USB Vendor ID(VID)와 Product ID(PID)
- HID 인터페이스 번호(MI)
- Usage Page와 Usage
- 기존 공급자와 동일한 배터리 요청/응답 프로토콜인지 여부

기본 프로필은 직접 편집하지 말고 사용자 프로필 파일을 가져오는 방식이 안전합니다. 아래 예시는 기존 F108 공급자와 **동일한 프로토콜임을 확인한** 새 PID를 현재 F108 카드에 적용하는 전체 덮어쓰기 프로필입니다.

```json
{
  "SchemaVersion": 1,
  "Profiles": [
    {
      "Id": "aula.f108-pro",
      "DisplayName": "교체한 F108 호환 키보드",
      "Category": "keyboard",
      "Icon": "keyboard",
      "ProviderId": "builtin.aula.f108",
      "Enabled": true,
      "DisplayOrder": 20,
      "PollSeconds": 30,
      "TimeoutMilliseconds": 1500,
      "LowBatteryPercent": 20,
      "Match": {
        "Transport": "hid",
        "VendorId": "0x05AC",
        "ProductIds": ["0x024F", "0x0000"],
        "InterfaceNumber": 3,
        "UsagePage": "0xFF60",
        "Usage": "0x0061",
        "XInputUserIndex": null
      },
      "ProviderOptions": {}
    }
  ]
}
```

`0x0000`은 예시이므로 실제 새 PID로 바꿔야 합니다. 이 JSON은 부분 패치가 아닙니다. 동일한 `Id`가 있으면 프로필 객체 전체가 사용자 버전으로 교체되므로 모든 필드를 포함하세요.

적용 절차:

1. 위 형식의 파일을 UTF-8 JSON으로 저장합니다.
2. 앱의 장치 관리 화면에서 **프로필 가져오기**를 선택합니다.
3. 앱을 완전히 종료한 뒤 다시 실행합니다.
4. 진단 정보에서 새 VID/PID와 선택한 HID 컬렉션을 확인합니다.
5. 판독값을 제조사 앱 또는 실제 배터리 상태와 비교해 검증합니다.

새 카드를 하나 더 표시하려면 기존 프로필과 다른 고유한 `Id`를 사용하고 `DisplayOrder`를 정합니다. 같은 `Id`는 덮어쓰기 용도입니다. 기본 장치를 숨기려면 동일한 `Id`의 전체 프로필을 사용자 파일에 넣고 `Enabled`를 `false`로 지정할 수 있습니다.

### 프로필 필드

| 필드 | 의미 |
|---|---|
| `SchemaVersion` | 현재 `1`만 지원 |
| `Id` | 병합 키. 장치마다 고유하게 지정 |
| `DisplayName` | GUI에 표시할 이름 |
| `Category` | `headset`, `keyboard`, `mouse`, `gamepad`, `other` 등 표시 분류 |
| `Icon` | 트레이 실루엣 힌트. `headset`, `keyboard`, `mouse`, `gamepad` 지원 |
| `ProviderId` | 배터리 공급자의 정확한 ID |
| `Enabled` | `false`면 로드 후 목록에서 제외 |
| `DisplayOrder` | 카드 정렬 순서 |
| `PollSeconds` | 프로필 기본 주기. 전역 앱 설정이 있으면 전역 값이 우선 |
| `TimeoutMilliseconds` | 개별 HID 입출력 제한 시간. 유효 범위는 구현에서 250~5000ms로 제한 |
| `LowBatteryPercent` | 낮은 배터리 알림 기준 |
| `Match.Transport` | 현재 `hid` 또는 `xinput` |
| `VendorId` / `ProductIds` | `0x` 접두사의 16진수 VID/PID. XInput에는 비워 둘 수 있음 |
| `InterfaceNumber` | HID 장치 경로의 MI 번호. 필요 없을 때 `null` |
| `UsagePage` / `Usage` | 일치시킬 HID top-level collection 값. 비우면 검사하지 않음 |
| `XInputUserIndex` | 특정 XInput 슬롯 0~3, 또는 모든 슬롯을 찾는 `null` |
| `ProviderOptions` | 공급자 전용 옵션. 해당 공급자가 문서화한 값만 사용 |

사용자 프로필은 `%LOCALAPPDATA%\PeripheralBatteryDashboard\Profiles`의 최상위 `*.json`에서 읽습니다. `Plugins` 아래의 `*.devices.json`도 재귀적으로 읽으며, 병합 우선순위는 기본 프로필 → 플러그인 프로필 → 사용자 프로필입니다.

`Icon`이 지원되는 값이면 트레이 아이콘의 기기 모양으로 사용합니다. 알 수 없는 값이면 `Category`를 다시 확인하고, 둘 다 지원되지 않으면 일반 장치 모양으로 표시합니다. 프로필을 추가하거나 비활성화한 후에는 앱을 재시작해야 트레이 아이콘 구성에 반영됩니다.

## 2. 새 프로토콜 공급자 플러그인 추가

명령 바이트, 보고서 종류(interrupt/output/feature), 응답 위치 또는 체크섬이 다르면 새 플러그인을 만듭니다. 플러그인은 다음 두 파일을 함께 배포하는 것을 권장합니다.

```text
Plugins\MyDevice\MyDevicePlugin.dll
Plugins\MyDevice\my-device.devices.json
```

DLL에는 공개 기본 생성자를 가진 `IBatteryProviderPlugin` 구현이 있어야 합니다. 앱은 `Plugins` 아래 DLL을 재귀적으로 로드하고 `CreateProviders()`가 반환한 공급자를 등록합니다.

```csharp
using System.Collections.Generic;
using PeripheralBatteryDashboard.Core;

public sealed class MyPlugin : IBatteryProviderPlugin
{
    public string PluginId { get { return "my-company.my-plugin"; } }

    public IEnumerable<IBatteryProvider> CreateProviders()
    {
        yield return new MyBatteryProvider();
    }
}
```

공급자는 고유한 `ProviderId`와 비동기 판독 메서드를 구현합니다.

```csharp
public sealed class MyBatteryProvider : IBatteryProvider
{
    public string ProviderId { get { return "my-company.my-device"; } }

    public async Task<BatteryReading> ReadAsync(
        DeviceProfile profile,
        BatteryReadContext context,
        CancellationToken cancellationToken)
    {
        HidDeviceDescriptor device = context.HidDevices.Find(profile);
        if (device == null)
            return BatteryReading.Unavailable(profile,
                DeviceConnectionState.Disconnected,
                "장치 연결 안 됨", "동글과 전원을 확인하세요.", "not-found");

        // 여기서 검증된 장치 프로토콜만 구현합니다.
        // HidSession.Open(device), cancellationToken, 프로필 timeout을 사용하세요.
        return BatteryReading.Unavailable(profile,
            DeviceConnectionState.Unsupported,
            "예제 공급자", "실제 프로토콜 구현이 필요합니다.", "example-only");
    }
}
```

전체 컴파일 가능 골격은 `Plugins\SamplePlugin.cs.txt`에 있습니다. `.cs.txt`를 `.cs`로 복사해 구현한 뒤 **배포 폴더의 `PeripheralBatteryDashboard.Runtime.dll`을 참조**해 클래스 라이브러리로 빌드합니다. GUI EXE나 Diagnostics EXE를 참조하면 타입 ID가 달라질 수 있으므로 사용하지 마세요.

VS 2022 Developer PowerShell의 예:

```powershell
csc /target:library /out:Plugins\MyDevice\MyDevicePlugin.dll `
  /reference:PeripheralBatteryDashboard.Runtime.dll `
  /reference:System.dll /reference:System.Core.dll `
  Plugins\MyDevice\MyBatteryProvider.cs
```

플러그인 프로필의 `ProviderId`는 DLL 공급자의 `ProviderId`와 정확히 같아야 합니다. 공급자 ID가 중복되면 해당 플러그인은 경고와 함께 로드되지 않습니다. 파일 변경 후 앱을 재시작하고 Diagnostics의 `--diagnostics`와 실제 기기로 확인하세요.

## 3. 프로토콜 구현 안전 수칙

HID 출력 보고서는 단순 조회가 아니라 DPI, 키맵, RF 페어링 또는 펌웨어 상태를 바꾸는 명령일 수 있습니다.

- 알 수 없는 바이트를 무작위로 보내거나 전체 명령 공간을 스캔하지 마세요.
- VID/PID뿐 아니라 인터페이스와 Usage까지 일치한 장치에만 명령을 보냅니다.
- 제조사 문서, 캡처 또는 검증된 구현으로 **읽기 전용 상태 요청**임을 확인한 보고서만 사용합니다.
- 보고서 ID, 전체 길이, 헤더, 체크섬을 모두 검증한 뒤 잔량을 해석합니다.
- 응답 범위가 0~100인지 확인하고 잘못된 값은 추정하지 말고 오류로 반환합니다.
- 모든 대기에는 `CancellationToken`과 `EffectiveTimeoutMilliseconds`를 적용합니다.
- 파일 핸들과 `HidSession`은 `using`으로 닫습니다.
- 절전, 연결 해제, 다른 앱의 장치 점유를 정상 상태로 처리하고 반복 실패 시 무한 재시도하지 않습니다.
- 플러그인 DLL은 앱과 같은 사용자 권한으로 실행됩니다. 소스와 배포자를 신뢰할 수 있는 플러그인만 설치하세요.

새 장치를 실사용하기 전에는 다른 제조사 설정 프로그램을 닫고, 중요한 온보드 프로필을 백업한 상태에서 한 번씩 수동 조회해 검증하는 것을 권장합니다.
