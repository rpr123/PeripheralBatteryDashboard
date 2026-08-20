# Peripheral Battery Dashboard

Windows에서 동글 또는 Bluetooth로 연결한 주변기기의 배터리 상태를 한 화면과 트레이에서 확인하는 앱입니다. 장치 식별 정보는 JSON 프로필에, 실제 조회 프로토콜은 공급자(provider)에 분리되어 있어 기기를 교체하거나 추가하기 쉽습니다.

> [!IMPORTANT]
> **처음 설치하거나 내 기기가 지원되는지 확인하려면, 아래 프롬프트를 코드 블록의 처음부터 끝까지 복사해 이 저장소를 열 수 있는 Codex 또는 코딩 LLM에 전달하세요.**  
> 로컬 Windows 장치와 파일에 접근할 수 없는 일반 채팅에서는 실제 설치·장치 진단을 끝낼 수 없습니다. Codex Desktop 등에서 이 저장소를 로컬로 연 뒤 전달하는 것을 권장합니다.

## Codex와 시작하기

이 앱은 실행할 때 Codex가 필요하지 않습니다. 다만 처음 설치하거나, 목록에 없는 동글형 주변기기를 추가할 때 Codex 같은 코딩 에이전트가 저장소의 문서·진단 결과·소스를 함께 검토하도록 설계했습니다.

1. 이 저장소를 내려받거나 복제합니다.
2. Codex 또는 로컬 Windows 셸과 저장소 파일에 접근할 수 있는 코딩 LLM에서 저장소 폴더를 엽니다.
3. 아래 프롬프트 **전체**를 그대로 전달합니다. 기기 이름과 연결 방식은 프롬프트 아래에 덧붙여도 됩니다.
4. 에이전트가 설치, 레지스트리 변경, 프로그램 다운로드 또는 검증된 적 없는 HID 명령 사용을 제안하면 내용을 먼저 확인하고 승인 여부를 결정합니다.

```text
이 저장소를 작업 대상으로 사용해 Peripheral Battery Dashboard를 내 Windows PC에 안전하게 설치하고, 연결된 주변기기와의 호환성을 점검해 줘.

반드시 작업 전에 저장소 루트의 README.md, AGENTS.md, CODEX-PROMPTS.md, DEVICE-ADDING.md를 모두 읽고 그 지침을 따라라.

진행 순서:
1. 먼저 읽기 전용으로 Windows 버전·아키텍처, .NET Framework 4.8, 빌드 도구, 저장소 상태를 확인하고 할 일을 요약한다.
2. 기존 배포본을 사용할지 소스에서 빌드할지 판단한다. 프로그램 설치, 다운로드, 레지스트리/자동 실행 변경, 저장소 밖 파일 변경 등 외부 상태를 바꾸기 전에는 대상과 영향을 설명하고 내 승인을 받는다. GUI 첫 실행은 기본 설정상 현재 사용자 자동 실행 레지스트리를 등록할 수 있으므로 실행 전에 이를 별도로 고지한다.
3. 빌드 또는 설치 후 장치 I/O를 하지 않는 PeripheralBatteryDashboard.Diagnostics.exe --self-test를 먼저 실행한다. --diagnostics와 --snapshot은 현재 매칭된 기존 공급자의 검증된 배터리 요청을 실제 장치로 보낼 수 있으므로, 이를 단순 파일 검사로 설명하지 말고 실행 전에 대상과 동작을 알린다. 진단 정보에서 장치 시리얼, 전체 장치 경로, 사용자명, 토큰과 그 밖의 식별 정보는 화면·로그·커밋에 노출하지 않는다.
4. 내 기기가 이미 지원되면 실제 판독 결과와 오류를 확인한다. 같은 검증된 프로토콜의 VID/PID 변형이라면 사용자 JSON 프로필만 추가하고, 기본 프로필을 무단으로 덮어쓰지 않는다.
5. 다른 프로토콜이면 IBatteryProvider 플러그인이 필요한지 설명한다. 제조사 문서, 사용자가 제공한 캡처, 또는 검증 가능한 기존 구현으로 읽기 전용 배터리 요청임이 확인되지 않았다면 장치에 어떤 HID interrupt/output/Feature write도 보내지 않는다. 이때 `지원 보류(blocked)`로 판정하고 필요한 증거를 구체적으로 보고하는 것은 올바른 완료 결과다.
6. 알 수 없는 HID 명령 전송, 무작위 바이트, 명령 공간 스캔/퍼징, 펌웨어·페어링·DPI·키맵·온보드 설정 변경은 절대 하지 않는다. 검증된 새 조회를 구현할 때도 VID/PID, 인터페이스(MI), Usage Page/Usage를 제한하고, 보고서 길이·전송 바이트·읽기 전용 근거와 예상 응답을 먼저 보여 준 뒤 첫 실기기 실행에 대한 별도 승인을 받는다. 길이·헤더·체크섬·응답 범위도 검증한다.
7. 변경은 필요한 프로필/공급자/문서와 테스트에만 한정한다. 빌드, --self-test와 관련 검증을 완료한 뒤 변경 파일, 결과, 남은 위험, 사용자가 승인해야 할 다음 단계를 보고한다. 내 요청 없이 GitHub 게시, 커밋, 푸시 또는 릴리스를 하지 않는다.

내 기기 정보:
- 제품명: (여기에 입력)
- 연결 방식: (Bluetooth / 2.4GHz USB 동글 / 유선)
- 현재 증상: (표시 안 됨 / 배터리 값이 틀림 / 설치 필요 등)
```

설치만 맡기거나 진단·미지원 기기 추가를 따로 요청하고 싶다면 [CODEX-PROMPTS.md](CODEX-PROMPTS.md)의 목적별 프롬프트를 사용하세요. 경량급 Codex 모델(예: Luna)은 문서에 정해진 설치·빌드·기존 공급자용 프로필 추가에 적합합니다. 알려지지 않은 동글 프로토콜을 근거 자료에서 분석하고 새 공급자를 구현하는 작업은 더 강한 추론 모델을 권장하며, 어떤 모델도 근거 없는 HID 명령을 시험해서는 안 됩니다.

## 다운로드와 공개 배포 안내

- Windows x64 배포본: [PeripheralBatteryDashboard-v1.0.4-win-x64.zip](releases/PeripheralBatteryDashboard-v1.0.4-win-x64.zip)
- SHA-256 목록: [releases/SHA256SUMS.txt](releases/SHA256SUMS.txt)

ZIP을 받은 뒤 PowerShell에서 다음 명령으로 해시를 계산하고 `SHA256SUMS.txt`의 값과 비교하세요.

```powershell
Get-FileHash .\PeripheralBatteryDashboard-v1.0.4-win-x64.zip -Algorithm SHA256
```

현재 배포 바이너리는 Authenticode 코드 서명이 되어 있지 않습니다. 따라서 Windows SmartScreen 또는 백신이 **알 수 없는 게시자** 경고를 표시할 수 있습니다. 경고를 무조건 우회하지 말고, 다운로드 출처와 SHA-256을 확인한 후 실행 여부를 결정하세요. 이 저장소의 소스를 직접 검토하고 아래 빌드 절차로 생성할 수도 있습니다.

요구 환경은 **Windows 10/11 x64와 .NET Framework 4.8**입니다. 기본 프로필은 아래 표의 정확한 기기 및 현재 확인된 하드웨어 매칭만 지원합니다. 같은 제품군, 다른 리비전 또는 다른 VID/PID가 자동으로 호환된다는 뜻이 아닙니다.

## 기본 지원 장치

| 장치 | 연결 | 표시 방식 |
|---|---|---|
| SteelSeries Arctis Nova 7 Gen 2 | 2.4GHz USB 동글 | 잔량 백분율, 충전 상태 |
| AULA F108 Pro | 2.4GHz USB 동글 | 잔량 백분율 |
| VXE R1 SE+ | 2.4GHz USB 동글 | 잔량 백분율, 충전 상태, 전압 |
| Xbox Wireless Controller | Bluetooth/XInput | Bluetooth GATT 백분율, 가능하면 XInput 4단계 잔량 |

현재 USB 동글 기본 매칭은 Arctis Nova 7 Gen 2 `1038:227E`(MI 03, Usage `FFC0:0001`), AULA F108 Pro `05AC:024F`(MI 03, Usage `FF60:0061`), VXE R1 SE+ `373B:1085`(MI 01, Usage `FF02:0002`)입니다. Xbox 기본 프로필은 `045E:0B13`과 Bluetooth/XInput 경로를 사용합니다. 실제 기준은 [Profiles/builtin.devices.json](Profiles/builtin.devices.json)이며, PID나 HID 컬렉션이 다르면 같은 제품명이어도 별도 확인이 필요합니다.

Xbox 컨트롤러는 연결 여부를 XInput 입력 상태로 확인합니다. Bluetooth 연결에서 XInput 배터리 정보가 비어 있으면 표준 Bluetooth Battery Service(GATT)를 읽어 백분율로 표시합니다. XInput이 건전지 잔량을 제공하는 연결 방식에서는 `교체 필요 / 부족 / 보통 / 충분` 4단계로 표시하며, 두 배터리 API 모두 값을 주지 않더라도 연결된 패드를 미연결로 오판하지 않습니다.

## 실행

1. 배포 ZIP을 폴더에 완전히 압축 해제합니다. 소스에서 빌드했다면 `dist` 폴더 전체를 원하는 위치에 둡니다. 실행 파일만 따로 옮기면 프로필과 공용 런타임을 찾지 못합니다.
2. `PeripheralBatteryDashboard.exe`를 실행합니다. 관리자 권한은 필요하지 않습니다.
3. 전원이 꺼지거나 절전 중인 장치는 먼저 버튼을 누르거나 움직여 깨운 뒤 **새로고침**을 누릅니다.
4. 창을 닫으면 기본 설정에서는 트레이로 최소화됩니다. 완전히 종료하려면 트레이 메뉴의 **종료**를 사용합니다.

트레이에 숨겨진 상태에서 실행 파일을 다시 열면 기존 창이 앞으로 나타납니다. 중복 모니터 프로세스는 만들지 않습니다.

기본 설정에서는 현재 Windows 사용자가 로그인할 때 앱이 창 없이 트레이에서 자동 실행됩니다. 자동 실행됐다는 별도 알림은 표시하지 않으며, 실제 배터리 부족 알림만 기존 설정에 따라 표시합니다. GUI의 **Windows 로그인 시 자동 실행** 체크박스로 언제든 켜거나 끌 수 있고 관리자 권한은 필요하지 않습니다.

설치 후 앱 폴더 경로를 유지하면 자동 실행 경로와 Windows의 트레이 아이콘 표시 설정이 덜 흔들립니다. 폴더를 옮겼다면 새 위치에서 한 번 직접 실행해 자동 실행 경로를 갱신하세요.

트레이 표시의 기본값은 **기기별 아이콘**입니다. 헤드셋·키보드·마우스·게임패드는 각각 다른 기기 실루엣으로 구분하고, 실루엣 안에 배터리 숫자와 충전·저전력 색상을 표시합니다. 마우스를 올리면 장치 이름·정확한 퍼센트·충전 또는 연결 상태를 확인할 수 있고, 왼쪽 클릭하면 대시보드가 열립니다. 설정에서 **통합 아이콘**으로 즉시 전환하면 특정 장치로 오해하지 않도록 별도의 공용 모양을 사용합니다. 이 표시는 기존 조회 결과를 재사용하므로 USB/Bluetooth 조회 횟수는 늘어나지 않습니다.

Windows 11은 새 트레이 아이콘을 숨겨진 아이콘 영역으로 접을 수 있습니다. 앱에서는 아이콘을 강제로 항상 표시할 수 없으므로, 필요한 경우 Windows의 **설정 → 개인 설정 → 작업 표시줄 → 기타 시스템 트레이 아이콘**에서 각 아이콘을 켜세요. 앱 폴더 경로가 바뀌면 Windows가 새 아이콘으로 인식할 수 있어 업데이트 후 이 설정을 다시 켜야 할 수도 있습니다.

요구 환경은 Windows 10/11 x64와 .NET Framework 4.8입니다. 제조사 프로그램이 같은 HID 인터페이스를 독점하고 있으면 `다른 앱이 장치 사용 중`으로 표시될 수 있습니다.

## 조회 주기와 시스템 부담

기본 조회 주기는 30초이며 설정에서 15, 30, 60, 120초를 선택할 수 있습니다. 한 번의 조회는 작은 HID/XInput 상태 요청뿐이고 동시에 두 장치까지만 처리하므로 15초도 일반적인 PC에서 부담이 매우 작습니다. 연결이 끊겼거나 응답하지 않는 장치는 실패가 반복될수록 조회 간격을 자동으로 늘리며 최대 5분까지 쉬었다가 재시도합니다.

Xbox Bluetooth 잔량은 첫 연결과 최대 5분마다만 패드에 직접 확인하고, 그 사이에는 Windows Bluetooth 캐시를 읽습니다. 따라서 앱의 15초 화면 갱신이 15초마다 컨트롤러를 깨우지는 않습니다.

배터리 절약을 우선하면 30초 또는 60초를 권장합니다. 15초 주기는 화면을 자주 확인하거나 충전 상태 변화를 빠르게 보고 싶을 때 사용하면 됩니다.

## 진단 도구

콘솔에서 다음 명령을 사용할 수 있습니다.

```powershell
.\PeripheralBatteryDashboard.Diagnostics.exe --snapshot
.\PeripheralBatteryDashboard.Diagnostics.exe --diagnostics
.\PeripheralBatteryDashboard.Diagnostics.exe --self-test
```

- `--snapshot`: 현재 배터리 판독값을 JSON으로 출력합니다. 매칭된 기존 공급자의 검증된 상태 요청을 실제 장치에 보낼 수 있습니다.
- `--diagnostics`: 배터리 상태, 적용 프로필, HID 컬렉션 정보를 텍스트로 출력합니다. 매칭된 기존 공급자의 검증된 상태 요청을 실제 장치에 보낼 수 있으며, 출력에서 장치 경로와 시리얼은 제외됩니다.
- `--self-test`: 프로필 형식, 체크섬, 공급자 등록 같은 앱 자체 구성을 검사합니다. 장치 연결 여부와 무관하고 실제 장치 요청 없이 실행할 수 있습니다.

GUI의 장치 관리 화면에서도 진단 정보를 파일로 내보낼 수 있습니다. 문의할 때는 이 파일을 사용하고, 장치 경로나 시리얼을 별도로 공유하지 마세요.

## 설정과 사용자 데이터

사용자 설정과 가져온 프로필은 다음 위치에 저장됩니다.

```text
%LOCALAPPDATA%\PeripheralBatteryDashboard\settings.json
%LOCALAPPDATA%\PeripheralBatteryDashboard\Profiles\devices.user.json
```

자동 실행은 현재 사용자의 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`에 등록됩니다. 앱 폴더를 삭제하기 전에는 GUI에서 자동 실행을 해제하는 것이 좋습니다.

앱 폴더의 `Profiles\builtin.devices.json`은 기본 프로필입니다. 업데이트 시 덮어쓸 수 있으므로 개인 변경은 GUI의 **프로필 가져오기** 또는 위 사용자 프로필 폴더를 이용하세요. 프로필과 플러그인은 시작할 때 읽으므로 변경 후 앱을 완전히 종료했다가 다시 실행해야 합니다.

장치 교체와 추가 방법은 [DEVICE-ADDING.md](DEVICE-ADDING.md)를 참고하세요.

## 소스에서 빌드

필요 항목:

- Visual Studio 2022 Build Tools의 **Desktop development with .NET** 구성 요소
- .NET Framework 4.8 Runtime 또는 Developer Pack
- Windows PowerShell 5.1 이상

프로젝트 폴더에서 실행합니다.

```powershell
PowerShell -ExecutionPolicy Bypass -File .\build.ps1
```

디버그 심볼과 최적화 해제 빌드가 필요하면 다음을 사용합니다.

```powershell
PowerShell -ExecutionPolicy Bypass -File .\build.ps1 -Configuration Debug
```

스크립트는 VS 2022 Roslyn 컴파일러와 .NET Framework 참조 어셈블리를 자동으로 찾습니다. 먼저 `PeripheralBatteryDashboard.Runtime.dll`을 만든 다음 GUI와 Diagnostics 실행 파일이 그 공용 DLL을 함께 참조하게 합니다. 이 구조 덕분에 외부 플러그인의 `IBatteryProvider` 타입이 두 실행 파일에서 동일하게 유지됩니다.

## 문제 해결

- **동글 연결 안 됨:** 동글을 다시 꽂고 장치의 전원을 켠 뒤 새로고침합니다. USB 허브 대신 본체 포트도 시험해 보세요.
- **절전 또는 응답 없음:** 키보드 키를 누르거나 마우스를 움직인 뒤 다시 조회합니다.
- **다른 앱이 장치 사용 중:** SteelSeries GG, 키보드/마우스 설정 도구 등 제조사 앱을 잠시 닫습니다.
- **Xbox 연결 안 됨:** 컨트롤러 전원을 켜고 Windows Bluetooth 페어링을 확인합니다. Steam Input이나 게임에서 먼저 잡은 슬롯도 XInput에서 검색됩니다.
- **지원 모듈 없음:** 프로필의 `ProviderId`와 설치된 플러그인의 `ProviderId`가 정확히 같은지 확인합니다.
- **플러그인이 로드되지 않음:** 파일 차단을 해제하고, 플러그인이 `PeripheralBatteryDashboard.Runtime.dll`을 참조해 빌드됐는지 확인합니다. 신뢰할 수 없는 DLL은 실행하지 마세요.

## 라이선스

앱 소스와 저장소 문서는 [MIT License](LICENSE)로 배포됩니다. 프로토콜 조사에 참고한 외부 자료와 별도 저작권 고지는 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)를 확인하세요.
