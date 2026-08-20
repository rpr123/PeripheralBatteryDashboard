# 장치 교체·추가 가이드

> [!IMPORTANT]
> 이 문서는 로컬 Codex 또는 코딩 에이전트가 읽는 구현 참고 자료입니다. 사용자가 프로필·HID 명령·플러그인 DLL을 직접 작성하거나 설치하는 절차가 아닙니다. 먼저 [CODEX-PROMPTS.md](CODEX-PROMPTS.md)의 목적별 프롬프트를 에이전트에 전달하세요.

이 앱은 두 층으로 장치를 지원합니다.

- **장치 프로필(JSON):** VID/PID, HID 인터페이스, Usage Page/Usage처럼 어떤 장치를 찾을지 지정합니다.
- **배터리 공급자(DLL의 `IBatteryProvider`):** 해당 장치에 어떤 요청을 보내고 응답에서 잔량을 어떻게 해석할지 구현합니다.

공개 배포본에는 활성 기본 기기 프로필이 없습니다. 에이전트는 각 PC에서 발견한 기기의 배터리 프로토콜을 공개 근거로 직접 조사해야 합니다. 기존 공급자와 같은 프로토콜임이 입증되면 사용자 JSON을 새로 등록하고, 명령과 응답 형식이 다르면 새 공급자 플러그인과 사용자 프로필을 구현합니다. 외형이 비슷하거나 제조사가 같다는 이유만으로 같은 프로토콜이라고 가정하면 안 됩니다.

## 기본 조사 흐름: 사용자의 프로토콜 자료를 기다리지 않기

> [!IMPORTANT]
> `프로토콜이 저장소에 없음`은 차단 사유가 아니라 조사 시작 조건입니다. 에이전트는 사용자에게 프로토콜을 찾아 달라고 넘기지 않고 네트워크 조사 승인을 요청한 뒤 공식 자료·검토 가능한 웹 드라이버·감사 가능한 오픈소스와 raw source를 직접 검색합니다. 아래 검색 행렬과 출처 종류를 실제로 확인한 기록 없이 즉시 `지원 보류(blocked)`를 선언하면 작업 실패입니다.

에이전트가 설치 프롬프트를 받으면 사용자가 제품 목록이나 프로토콜 자료를 채우지 않았더라도 설치와 `--self-test` 후 미지원 기기 조사를 계속합니다. 제품이 여러 개 적혀 있으면 모두 같은 작업의 대상이며, 기기별 identity·근거·구현·검증 결과를 별도로 추적합니다. 독립적인 조사는 안전한 범위에서 병렬로 수행하고, 한 기기의 실패나 지원 보류 때문에 나머지를 생략하지 않습니다. 기본 절차는 다음과 같습니다.

1. `--inventory`의 정확한 실행 파일, 열거 대상과 redacted 출력 필드를 설명하고 사용자의 승인을 받습니다. 승인 후 에이전트가 실행합니다. 이 명령은 Windows에 HID collection으로 노출된 USB 동글·유선·Bluetooth HID descriptor와 등록된 표준 Bluetooth Battery Service 인터페이스 메타데이터를 열거합니다. provider/battery 요청, HID input read·output write·Feature I/O와 GATT characteristic 값 읽기는 전혀 수행하지 않습니다. 종료 코드 0, `complete=true`, `profileWarningCount=0`을 확인한 경우에만 결과를 분류에 사용하며, 실패하거나 경고가 있으면 빈 목록을 장치 없음으로 해석하지 않습니다. HID의 VID/PID·MI·Usage·보고서 길이와 `bestEffortSanitizedProductString`, Bluetooth의 가능한 `vendorIdSource`·VID/PID·best-effort 이름·`localServiceId`를 사용하되 이름은 외부 검색 전에 로컬에서 재검토하고 가명 ID는 외부로 보내지 않습니다.
2. `bluetoothBatteryServices` 항목이 있으면 Bluetooth SIG 표준 Battery Service `180F`와 Battery Level `2A19` 프로토콜이 확인된 것입니다. 브랜드별 공급자를 새로 만들지 않고 `builtin.bluetooth.gatt-battery`, `Transport=bluetooth-gatt` 사용자 프로필을 먼저 사용합니다. 해당 항목의 이 PC 전용 `localServiceId`를 항상 사용하고, exact VID/PID가 있으면 추가 AND 조건으로 함께 기록합니다. 같은 조건의 서비스가 여러 개면 첫 항목을 고르지 않고 검토한 이름 보조 조건으로 더 좁힙니다. `localServiceId`는 외부 검색·공개 프로필·이슈에 사용하지 않습니다.
3. HID에서는 로컬 검토를 마친 제품 문자열과 VID/PID가 분명한 `researchCandidate=true` 후보를 곧바로 기기별 조사 대상으로 만듭니다. exact selector 일치는 공급자 등록이나 실제 조회 성공의 증명이 아니며, broad selector는 계속 재검토 후보입니다. `BT5.3 MOUSE CH1`이나 `2.4G/USB Receiver`처럼 모호하면 해당 기기만 켠/끄거나 해당 동글만 연결/분리한 전후의 `--inventory`를 각각 승인받아 에이전트가 안정적인 `deviceGroupId`와 HID identity tuple의 redacted 차이를 비교합니다. 그래도 식별되지 않을 때만 정확한 제품명이나 시리얼·바코드를 가린 라벨 사진을 한 번 최소 질문으로 요청합니다.
4. `--inventory`는 표준 Battery Service가 없는 비 HID Bluetooth, XInput-only·audio-only 장치의 자동 발견을 보장하지 않습니다. 기기 목록이 비어 있으면 에이전트는 현재 연결된 PnP·Bluetooth·오디오 엔드포인트·XInput/game-controller의 읽기 전용·redacted Windows 메타데이터 점검도 별도 승인받아 기본 수행합니다. 이 점검은 공급자나 장치 요청을 실행해서는 안 됩니다. 그래도 비 HID 주변기기 존재를 확정할 수 없을 때만 배터리 주변기기가 더 있는지 한 번의 통합 최소 질문을 합니다. 완전 유선이거나 배터리가 없는 장치는 제조사 사양 또는 Windows 메타데이터 근거가 있을 때만 배터리 비대상으로 제외합니다.
5. 표준 Battery Service가 없는 후보의 네트워크 조사 전에 검색 범위와 외부로 전달될 식별 정보를 설명하고 승인을 받습니다. 승인되면 미지원 후보를 가능한 범위에서 병렬로 조사합니다. 출처 우선순위는 제조사 공식 제품·지원·다운로드 문서 → 법적으로 검토 가능한 제조사 웹 드라이버 자산 → 감사 가능한 기기별 오픈소스 구현입니다. exact VID:PID의 여러 표기, 제품 문자열, MI·Usage·보고서 길이, 안정적인 캡처 prefix와 패킷 내부 device/model ID의 16진수·10진수 조합으로 검색 행렬을 만들고 문서와 공개 raw source/code를 모두 확인합니다. 첫 VID/PID 결과나 소매 모델 저장소 하나로 검색을 끝내지 않으며, 지원 보류 시 실제 질의 변형을 남깁니다. 각 URL, 정확한 버전·커밋 또는 확인 날짜, 적용되는 하드웨어 리비전·연결 모드와 라이선스를 기록합니다. 인터넷에서 받은 실행 파일이나 DLL은 실행하지 않고, 출처 불명 또는 라이선스가 없거나 맞지 않는 코드는 복사하지 않습니다.
6. `--inventory`의 exact identity를 자료의 VID/PID·인터페이스·Usage·보고서 종류·바이트와 대응시킵니다. 공유 VID/PID와 패킷 내부 device/model ID가 서로 다른 제품을 가리키면 첫 검색 결과로 모델명을 정하지 않고 복수 출처로 해소합니다. 기존 공급자를 재사용할 때는 helper뿐 아니라 `ReadAsync`의 발견·연결 판정·identity binding 전체를 확인하며, 모든 XInput 슬롯을 순회하는 공급자를 여러 프로필에 붙여 중복 표시하지 않습니다. 기존 읽기 전용 요청/응답과 같으면 좁은 JSON 프로필과 fixture를 만들고, 다르면 독립 공급자·프로필과 정상/오류 모의 응답 fixture를 구현합니다. 제공되거나 승인된 캡처와 문서에 서로 다른 상태/subtype이 있으면 모든 표본을 이름 있는 fixture로 만들고 충전·방전·유선·절전·사용 불가 상태를 각각 매핑합니다. 일부 표본을 조용히 버리지 않고, 알 수 없는 subtype은 거부하며, 근거 없는 패딩·후행 0을 정상 조건으로 추가하지 않습니다. 새 프로토콜이나 공급자는 구현과 분리된 독립 검토가 원본 근거·모든 캡처·fixture·전체 `ReadAsync`를 다시 확인한 뒤에만 적용합니다. 독립 검토가 없거나 불일치가 남으면 프로필/플러그인을 설치·로드하지 않고 미검증/부분 지원으로 둡니다. 송신이 없는 수동 입력 공급자는 `HidSession.OpenReadOnly`만 사용합니다. 실제 장치 요청 전에 격리된 Release 빌드와 `--self-test`를 종료 코드 0으로 완료하며, 실행하지 못했거나 실패하면 지원 완료로 표시하지 않습니다.
7. 공개 자료를 실제로 조사했지만 exact hardware identity 또는 읽기 전용 배터리 프로토콜을 확정할 수 없는 경우에만 실제 검색 질의·확인한 URL과 출처 종류·부족한 증거를 적어 `지원 보류(blocked)`로 끝냅니다. 사용자가 프로토콜 자료를 처음부터 주지 않았거나 기본 목록에 이름이 없다는 사실만으로 보류하지 않습니다. 추측 명령·퍼징 금지와 첫 실기기 명령의 별도 승인 경계는 아래 안전 수칙을 그대로 따릅니다.

## 1. 기존 공급자를 쓰는 장치로 교체

에이전트는 먼저 redacted `--inventory`와 위 공개 자료 조사에서 다음 값을 확인합니다. 사용자가 별도 자료를 제공한 경우에는 추가 근거로 검토합니다. `--diagnostics`나 `--snapshot`을 새로 실행해야 한다면 대상 ProviderId·장치 매칭·수행될 I/O를 먼저 설명하고 사용자의 명시적 확인을 기다립니다.

- USB Vendor ID(VID)와 Product ID(PID)
- HID 인터페이스 번호(MI)
- Usage Page와 Usage
- 기존 공급자와 동일한 배터리 요청/응답 프로토콜인지 여부

에이전트는 의도적으로 비어 있는 기본 프로필을 편집하지 않고 사용자 프로필 파일로 적용해야 합니다. 아래 예시는 기존 F108 공급자와 **동일한 프로토콜임을 공개 근거로 확인한** 장치를 새 사용자 카드로 등록하는 완전한 프로필입니다.

```json
{
  "SchemaVersion": 1,
  "Profiles": [
    {
      "Id": "local.f108-compatible-keyboard",
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
        "ProductIds": ["0x0000"],
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

`0x0000`은 예시이므로 실제 PID로 바꿔야 합니다. 이 JSON은 부분 패치가 아닙니다. 동일한 `Id`가 사용자 파일에 이미 있으면 프로필 객체 전체가 새 버전으로 교체되므로 모든 필드를 포함하세요.

표준 Bluetooth Battery Service가 인벤토리에 보인 장치는 새 플러그인 없이 다음 형태를 사용합니다. 같은 인벤토리 항목의 `localServiceId`를 `BluetoothServiceId`에 반드시 넣습니다. VID/PID가 보이면 아래처럼 추가 AND 조건으로 기록하고, 보이지 않으면 두 필드를 비웁니다.

```json
{
  "SchemaVersion": 1,
  "Profiles": [
    {
      "Id": "local.standard-bluetooth-device",
      "DisplayName": "내 Bluetooth 기기",
      "Category": "other",
      "Icon": "other",
      "ProviderId": "builtin.bluetooth.gatt-battery",
      "Enabled": true,
      "DisplayOrder": 30,
      "PollSeconds": 30,
      "TimeoutMilliseconds": 1500,
      "LowBatteryPercent": 20,
      "Match": {
        "Transport": "bluetooth-gatt",
        "VendorId": "0x1234",
        "ProductIds": ["0x5678"],
        "BluetoothServiceId": "bt-bas-0123456789abcdef01234567"
      },
      "ProviderOptions": {}
    }
  ]
}
```

위 `BluetoothServiceId`는 형식 예시일 뿐입니다. 반드시 같은 PC에서 실행한 최신 인벤토리 항목의 실제 `localServiceId`로 바꿉니다.

에이전트 적용 절차:

1. 전체 JSON, 대상 사용자 프로필 경로와 기존 파일 보존 방식을 사용자에게 보여 주고 쓰기 승인을 받습니다.
2. 승인 후 UTF-8 JSON으로 저장하고 에이전트가 사용자 프로필 폴더에 적용합니다. GUI의 **프로필 가져오기**는 아래 GUI 실행 승인 절차를 먼저 완료한 경우에만 대신 사용할 수 있습니다.
3. 장치 I/O가 없는 `--self-test`와 프로필 fixture 검증을 먼저 통과시킵니다.
4. GUI 실행 또는 앱 재시작이 자동 실행 동기화와 exact-match 공급자의 장치 요청을 시작할 수 있음을 설명하고 사용자의 명시적 확인을 받은 뒤 에이전트가 실행·재시작합니다.
5. `--diagnostics`가 필요하면 대상 ProviderId·장치 매칭·수행될 I/O를 설명하고 별도 확인을 받은 뒤 새 VID/PID와 선택한 HID 컬렉션을 확인합니다.
6. 사용자가 허용한 범위에서만 판독값을 제조사 앱 또는 알려진 실제 배터리 상태와 비교합니다.

장치마다 고유한 `Id`와 `DisplayOrder`를 정합니다. 같은 `Id`는 기존 사용자 프로필을 전체 교체할 때만 사용합니다. 등록된 프로필이라도 해당 PC에서 exact hardware가 현재 감지되지 않으면 배터리 카드와 기기별 트레이 아이콘에는 나타나지 않습니다.

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
| `Match.Transport` | 정확히 `hid`, `bluetooth-gatt`, `xinput` 중 하나 |
| `VendorId` / `ProductIds` | `0x` 접두사의 16진수 VID/PID. Xbox GATT와 모든 HID 프로필에는 exact 값이 필수. 범용 `bluetooth-gatt`에서는 선택적인 추가 AND 조건이며 둘 다 지정하거나 둘 다 비움 |
| `InterfaceNumber` | HID 장치 경로의 MI 번호. 번호가 실제로 없으면 `null`과 `RequireNoInterfaceNumber=true`를 함께 사용 |
| `RequireNoInterfaceNumber` | 장치 경로에 MI 구성요소가 없음을 exact 조건으로 요구. `InterfaceNumber`와 동시에 지정하면 오류 |
| `UsagePage` / `Usage` | 일치시킬 HID top-level collection 값. 실제 provider I/O를 하는 HID 프로필에는 둘 다 필수 |
| `BluetoothServiceId` | 표준 BAS 장치를 이 PC에서만 고정하는 `bt-bas-` + 24자리 16진수 가명 ID. `builtin.bluetooth.gatt-battery` 프로필에 필수이며 다른 공급자에서는 사용하지 않고 외부 공유도 금지 |
| `XInputUserIndex` | 특정 XInput 슬롯 0~3. 슬롯은 VID/PID를 제공하지 않으므로 `null` 전체 순회는 배터리 identity로 사용할 수 없음 |
| `ProviderOptions` | 공급자 전용 옵션. 해당 공급자가 문서화한 값만 사용 |

`InterfaceNumber=null`만 두면 MI를 검사하지 않는 broad selector이므로 provider I/O가 차단됩니다. 실제 장치 경로에 `MI_XX`가 없음을 descriptor 인벤토리로 확인했을 때만 `RequireNoInterfaceNumber=true`를 사용합니다.

Xbox 공급자는 암묵적인 Microsoft VID/PID를 보충하지 않습니다. exact Bluetooth GATT 경로에는 프로필의 VID/PID가 반드시 있어야 합니다. `AllowUnboundXInput=true`는 `XInputUserIndex`가 함께 있을 때만 적용됩니다. XInput 슬롯과 VID/PID를 영구적으로 연결하는 기능이 아니므로, exact Bluetooth GATT를 사용할 수 없는 연결을 에이전트가 별도로 검증하고 한계를 사용자에게 설명한 경우가 아니면 설정하지 않습니다.

일반 Bluetooth 기기가 표준 Battery Service `180F/2A19`를 제공하면 `ProviderId="builtin.bluetooth.gatt-battery"`, `Transport="bluetooth-gatt"`를 사용합니다. 인벤토리의 유효한 `BluetoothServiceId`가 필수이고, exact VID/PID는 선택적인 추가 AND 조건입니다. 같은 VID/PID의 서비스가 여러 개이고 인벤토리의 이름이 안정적이라고 검토한 경우에만 `ProviderOptions`에 `"BluetoothNameContains": "검토한 이름 일부"`를 넣어 추가 AND 조건으로 좁힐 수 있습니다. 이름이 없거나 달라지면 안전하게 매칭하지 않습니다. 이 표준 특성은 잔량만 제공하므로 충전 상태를 추정하지 않습니다. 표준 서비스가 감지됐지만 일시적으로 값을 읽지 못한 경우 장치를 숨기지 않고 잔량 미확인 상태로 유지하며, 같은 조건의 서비스가 여러 개면 임의의 첫 장치를 읽지 않습니다.

표준 Battery Service가 없어 제조사 전용 GATT 프로토콜 공급자를 구현한 경우에도 `Transport="bluetooth-gatt"`를 사용하지만, BAS용 `BluetoothServiceId`는 넣지 않습니다. 대신 해당 공급자 프로필에는 exact VID/PID가 필수이며, 서비스 UUID와 특성 UUID·읽기 방식·기기 식별은 공급자 코드와 근거 문서에서 별도로 좁혀야 합니다.

사용자 프로필은 `%LOCALAPPDATA%\PeripheralBatteryDashboard\Profiles`의 최상위 `*.json`에서 읽습니다. `Plugins` 아래의 `*.devices.json`도 재귀적으로 읽으며, 병합 우선순위는 빈 기본 문서 → 플러그인 프로필 → 사용자 프로필입니다. 공개 배포 패키지는 플러그인 자동 등록 프로필도 포함하지 않습니다.

`Icon`이 지원되는 값이면 트레이 아이콘의 기기 모양으로 사용합니다. 알 수 없는 값이면 `Category`를 다시 확인하고, 둘 다 지원되지 않으면 일반 장치 모양으로 표시합니다. 프로필을 추가하거나 비활성화한 후에는 앱을 재시작해야 트레이 아이콘 구성에 반영됩니다.

## 2. 새 프로토콜 공급자 플러그인 추가

명령 바이트, 보고서 종류(interrupt/output/feature), 응답 위치 또는 체크섬이 다르면 새 플러그인을 만듭니다. 에이전트는 사용자가 프로토콜 문서를 주기를 기다리지 않고 위 출처 우선순위로 조사하며, exact device/revision과 읽기 전용 요청을 연결하는 URL·고정 버전/커밋·라이선스를 구현 근거로 남깁니다. 플러그인은 다음 두 파일을 함께 배포하는 것을 권장합니다.

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
- 웹 드라이버나 오픈소스를 근거로 쓸 때는 정확한 URL·버전/커밋·라이선스와 대상 하드웨어 매칭을 기록합니다. 라이선스가 없거나 호환되지 않는 코드는 복사하지 않습니다.
- 보고서 ID, 전체 길이, 헤더, 체크섬을 모두 검증한 뒤 잔량을 해석합니다.
- 응답 범위가 0~100인지 확인하고 잘못된 값은 추정하지 말고 오류로 반환합니다.
- 모든 대기에는 `CancellationToken`과 `EffectiveTimeoutMilliseconds`를 적용합니다.
- 파일 핸들과 `HidSession`은 `using`으로 닫습니다.
- 절전, 연결 해제, 다른 앱의 장치 점유를 정상 상태로 처리하고 반복 실패 시 무한 재시도하지 않습니다.
- 플러그인 DLL은 앱과 같은 사용자 권한으로 실행됩니다. 에이전트가 소스·배포자·해시를 검토하고 변경 파일과 영향을 보여 준 뒤 사용자가 승인한 플러그인만 배치합니다. 검토되지 않은 DLL의 차단을 해제하거나 로드하지 않습니다.

새 장치를 실사용하기 전에는 모의 테스트를 먼저 완료합니다. 첫 실기기 요청 전 에이전트가 정확한 VID/PID·MI·Usage·보고서 길이·전송 바이트·읽기 전용 근거·예상 응답·실패 영향을 제시하고 별도 승인을 기다립니다. 승인된 1회 검증에서만 다른 제조사 설정 프로그램을 닫고 판독값을 알려진 배터리 상태와 비교합니다.
