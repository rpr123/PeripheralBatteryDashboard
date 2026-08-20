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

플러그인은 반드시 현재 배포본의 `PeripheralBatteryDashboard.Runtime.dll`을 참조해 빌드하세요. `PeripheralBatteryDashboard.exe` 또는 `PeripheralBatteryDashboard.Diagnostics.exe`를 참조하지 마세요. 두 실행 파일은 진입점만 담고 있으며, 공용 API의 타입 ID는 Runtime DLL에 있습니다.

`SamplePlugin.cs.txt`는 로딩 가능한 최소 골격입니다. 안전을 위해 실제 HID 명령은 보내지 않으며, 장치 프로토콜을 확인한 개발자가 `ReadAsync`를 구현해야 합니다. 플러그인과 프로필을 추가한 뒤 앱을 완전히 종료하고 다시 실행하세요.

주의: DLL은 앱 프로세스 안에서 현재 사용자 권한으로 실행됩니다. 출처를 신뢰하고 코드를 검토할 수 있는 플러그인만 넣으세요. 인터넷에서 받은 DLL은 Windows 파일 속성의 차단 여부도 확인하세요.
