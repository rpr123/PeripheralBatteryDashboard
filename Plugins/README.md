# Plugins 폴더

> [!IMPORTANT]
> 이 디렉터리는 검토 가능한 소스에서 에이전트가 플러그인을 빌드·검증할 때 사용하는 개발 자료입니다. 사용자는 인터넷에서 받은 DLL을 직접 복사하거나 설치하지 말고 [CODEX-PROMPTS.md](../CODEX-PROMPTS.md)의 기기 지원 프롬프트를 로컬 에이전트에 전달하세요.

새 배터리 프로토콜을 지원하는 확장 모듈을 이 폴더 아래에 둡니다. 공개 배포본은 활성 기본 기기 또는 플러그인 프로필을 포함하지 않으며, 에이전트가 각 PC의 실제 기기에 대해 프로토콜을 직접 조사·검증한 뒤 승인받은 공급자와 사용자 프로필만 등록합니다. 앱은 시작할 때 하위 폴더를 포함해 다음 파일을 찾습니다.

- `*.dll`: `PeripheralBatteryDashboard.Core.IBatteryProviderPlugin` 구현을 검색하고 공급자를 등록합니다.
- `*.devices.json`: 플러그인 공급자를 사용할 장치 프로필을 읽습니다.

권장 배치:

```text
Plugins\Vendor.Device\Vendor.Device.Plugin.dll
Plugins\Vendor.Device\vendor-device.devices.json
Plugins\Vendor.Device\README.md
```

에이전트는 반드시 현재 검증 대상 배포본의 `PeripheralBatteryDashboard.Runtime.dll`만 참조해 플러그인을 격리된 출력 폴더에 빌드합니다. `PeripheralBatteryDashboard.exe` 또는 `PeripheralBatteryDashboard.Diagnostics.exe`를 참조하지 않습니다. 두 실행 파일은 진입점만 담고 있으며, 공용 API의 타입 ID는 Runtime DLL에 있습니다.

`SamplePlugin.cs.txt`는 로딩 가능한 최소 골격입니다. 안전을 위해 실제 HID 명령은 보내지 않습니다. 에이전트는 사용자가 프로토콜 자료를 제공하기를 기다리지 말고, redacted `--inventory`의 exact HID identity를 기준으로 승인된 네트워크 조사에서 제조사 공식 문서·법적으로 검토 가능한 웹 드라이버 자산·감사 가능한 기기별 오픈소스를 순서대로 확인합니다. `--inventory`는 HID descriptor와 표준 Bluetooth Battery Service 인터페이스 메타데이터만 열거하며 provider/battery 요청, HID input read·output write·Feature I/O와 GATT characteristic 값 읽기를 수행하지 않습니다. Bluetooth가 표준 `180F/2A19`를 제공하면 플러그인을 만들지 않고 `builtin.bluetooth.gatt-battery` 사용자 프로필을 우선 사용합니다. 근거에는 URL, 정확한 버전/커밋 또는 확인 날짜, 적용 리비전·연결 모드와 라이선스를 남기고, 출처 불명 또는 라이선스가 없거나 맞지 않는 코드는 복사하지 않습니다.

exact hardware identity와 읽기 전용 배터리 프로토콜이 입증되면 에이전트가 `ReadAsync`를 구현하고 정상·오류 모의 응답 fixture, 프로필 fixture, Release 빌드와 `--self-test`를 실제 장치 요청 전에 종료 코드 0으로 통과시켜야 합니다. 송신이 전혀 없는 수동 입력 공급자는 `HidSession.OpenReadOnly`와 input read만 사용하고, 기존 공급자를 재사용할 때는 helper가 아니라 발견·연결 판정·identity binding 전체를 검토합니다. 컴파일이나 테스트가 실행되지 않았거나 실패하면 지원 완료로 표시하지 않습니다. 공개 자료를 실제로 조사한 뒤에도 근거가 부족한 경우에만 검색한 출처와 부족한 증거를 적어 지원을 보류합니다. 플러그인과 프로필의 대상 경로·파일·영향을 보여 주고 승인을 받은 뒤 에이전트가 배치합니다. 앱 재시작이 exact-match 공급자의 실제 장치 요청을 시작할 수 있음을 설명하고 별도 확인을 받은 경우에만 재시작합니다.

주의: DLL은 앱 프로세스 안에서 현재 사용자 권한으로 실행됩니다. 에이전트는 소스·배포자·해시를 검토할 수 있는 플러그인만 제안하고 변경 내용을 보여 준 뒤 사용자 승인을 받아 배치합니다. 검토되지 않은 인터넷 DLL은 차단을 해제하거나 로드하지 않습니다.
