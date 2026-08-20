# 장치 교체·추가 가이드

> [!IMPORTANT]
> 이 문서는 로컬 Codex 또는 코딩 에이전트가 읽는 구현 참고 자료입니다. 사용자가 프로필·HID 명령·플러그인 DLL을 직접 작성하거나 설치하는 절차가 아닙니다. 먼저 [CODEX-PROMPTS.md](CODEX-PROMPTS.md)의 목적별 프롬프트를 에이전트에 전달하세요.

이 앱은 두 층으로 장치를 지원합니다.

- **장치 프로필(JSON):** VID/PID, HID 인터페이스, Usage Page/Usage처럼 어떤 장치를 찾을지 지정합니다.
- **배터리 공급자(DLL의 `IBatteryProvider`):** 해당 장치에 어떤 요청을 보내고 응답에서 잔량을 어떻게 해석할지 구현합니다.

같은 프로토콜을 쓰는 기기로 바뀌었다면 JSON만 추가하거나 덮어쓰면 됩니다. 명령과 응답 형식이 다른 기기는 새 공급자 플러그인이 필요합니다. 외형이 비슷하거나 제조사가 같다는 이유만으로 같은 프로토콜이라고 가정하면 안 됩니다.

## 1. 기존 공급자를 쓰는 장치로 교체

에이전트는 Windows 장치 관리자, 사용자가 제공한 redacted 진단 또는 제조사 문서에서 다음 값을 확인합니다. `--diagnostics`나 `--snapshot`을 새로 실행해야 한다면 대상 ProviderId·장치 매칭·수행될 I/O를 먼저 설명하고 사용자의 명시적 확인을 기다립니다.

- USB Vendor ID(VID)와 Product ID(PID)
- HID 인터페이스 번호(MI)
- Usage Page와 Usage
- 기존 공급자와 동일한 배터리 요청/응답 프로토콜인지 여부

에이전트는 기본 프로필을 직접 편집하지 않고 사용자 프로필 파일로 적용해야 합니다. 아래 예시는 기존 F108 공급자와 **동일한 프로토콜임을 확인한** 새 PID를 현재 F108 카드에 적용하는 전체 덮어쓰기 프로필입니다.

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

에이전트 적용 절차:

1. 전체 JSON, 대상 사용자 프로필 경로와 기존 파일 보존 방식을 사용자에게 보여 주고 쓰기 승인을 받습니다.
2. 승인 후 UTF-8 JSON으로 저장하고 에이전트가 사용자 프로필 폴더에 적용합니다. GUI의 **프로필 가져오기**는 아래 GUI 실행 승인 절차를 먼저 완료한 경우에만 대신 사용할 수 있습니다.
3. 장치 I/O가 없는 `--self-test`와 프로필 fixture 검증을 먼저 통과시킵니다.
4. GUI 실행 또는 앱 재시작이 자동 실행 동기화와 exact-match 공급자의 장치 요청을 시작할 수 있음을 설명하고 사용자의 명시적 확인을 받은 뒤 에이전트가 실행·재시작합니다.
5. `--diagnostics`가 필요하면 대상 ProviderId·장치 매칭·수행될 I/O를 설명하고 별도 확인을 받은 뒤 새 VID/PID와 선택한 HID 컬렉션을 확인합니다.
6. 사용자가 허용한 범위에서만 판독값을 제조사 앱 또는 알려진 실제 배터리 상태와 비교합니다.

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

전체 컴파일 가능 골격은 `Plugins\SamplePlugin.cs.txt`에 있습니다. 에이전트는 이를 격리된 작업 폴더의 `.cs`로 복사해 구현한 뒤 **검증 대상 배포본의 `PeripheralBatteryDashboard.Runtime.dll`만 참조**해 클래스 라이브러리로 빌드합니다. GUI EXE나 Diagnostics EXE를 참조하면 타입 ID가 달라질 수 있으므로 사용하지 않습니다. 빌드·배치·검증은 [AGENTS.md](AGENTS.md)의 승인 및 안전 계약을 따릅니다.

VS 2022 Developer PowerShell의 예:

```powershell
csc /target:library /out:Plugins\MyDevice\MyDevicePlugin.dll `
  /reference:PeripheralBatteryDashboard.Runtime.dll `
  /reference:System.dll /reference:System.Core.dll `
  Plugins\MyDevice\MyBatteryProvider.cs
```

플러그인 프로필의 `ProviderId`는 DLL 공급자의 `ProviderId`와 정확히 같아야 합니다. 공급자 ID가 중복되면 해당 플러그인은 경고와 함께 로드되지 않습니다. 에이전트는 모의 응답 테스트, 격리된 Release 빌드와 `--self-test`를 먼저 통과시킵니다. 그 뒤 exact target·보고서 종류와 전체 바이트·읽기 전용 근거를 제시해 별도 승인을 받은 경우에만 앱을 재시작하거나 `--diagnostics`와 실제 기기 판독을 수행합니다.

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
- 플러그인 DLL은 앱과 같은 사용자 권한으로 실행됩니다. 에이전트가 소스·배포자·해시를 검토하고 변경 파일과 영향을 보여 준 뒤 사용자가 승인한 플러그인만 배치합니다. 검토되지 않은 DLL의 차단을 해제하거나 로드하지 않습니다.

새 장치를 실사용하기 전에는 모의 테스트를 먼저 완료합니다. 첫 실기기 요청 전 에이전트가 정확한 VID/PID·MI·Usage·보고서 길이·전송 바이트·읽기 전용 근거·예상 응답·실패 영향을 제시하고 별도 승인을 기다립니다. 승인된 1회 검증에서만 다른 제조사 설정 프로그램을 닫고 판독값을 알려진 배터리 상태와 비교합니다.
