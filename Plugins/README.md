# Plugins 폴더

> [!IMPORTANT]
> 이 디렉터리는 검토 가능한 소스에서 에이전트가 플러그인을 빌드·검증할 때 사용하는 개발 자료입니다. 사용자는 인터넷에서 받은 DLL을 직접 복사하거나 설치하지 말고 [CODEX-PROMPTS.md](../CODEX-PROMPTS.md)의 기기 지원 프롬프트를 로컬 에이전트에 전달하세요.

새 배터리 프로토콜을 지원하는 확장 모듈을 이 폴더 아래에 둡니다. 앱은 시작할 때 하위 폴더를 포함해 다음 파일을 찾습니다.

- `*.dll`: `PeripheralBatteryDashboard.Core.IBatteryProviderPlugin` 구현을 검색하고 공급자를 등록합니다.
- `*.devices.json`: 플러그인 공급자를 사용할 장치 프로필을 읽습니다.

권장 배치:

```text
Plugins\Vendor.Device\Vendor.Device.Plugin.dll
Plugins\Vendor.Device\vendor-device.devices.json
Plugins\Vendor.Device\README.md
```

에이전트는 반드시 현재 검증 대상 배포본의 `PeripheralBatteryDashboard.Runtime.dll`만 참조해 플러그인을 격리된 출력 폴더에 빌드합니다. `PeripheralBatteryDashboard.exe` 또는 `PeripheralBatteryDashboard.Diagnostics.exe`를 참조하지 않습니다. 두 실행 파일은 진입점만 담고 있으며, 공용 API의 타입 ID는 Runtime DLL에 있습니다.

`SamplePlugin.cs.txt`는 로딩 가능한 최소 골격입니다. 안전을 위해 실제 HID 명령은 보내지 않으며, 장치 프로토콜을 확인한 에이전트가 `ReadAsync`를 구현하고 모의 응답 테스트·Release 빌드·`--self-test`를 먼저 통과시켜야 합니다. 플러그인과 프로필의 대상 경로·파일·영향을 보여 주고 승인을 받은 뒤 에이전트가 배치합니다. 앱 재시작이 exact-match 공급자의 실제 장치 요청을 시작할 수 있음을 설명하고 별도 확인을 받은 경우에만 재시작합니다.

주의: DLL은 앱 프로세스 안에서 현재 사용자 권한으로 실행됩니다. 에이전트는 소스·배포자·해시를 검토할 수 있는 플러그인만 제안하고 변경 내용을 보여 준 뒤 사용자 승인을 받아 배치합니다. 검토되지 않은 인터넷 DLL은 차단을 해제하거나 로드하지 않습니다.
