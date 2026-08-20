# Third-party notices

이 배포본에는 타사 실행 파일, 라이브러리 또는 소스 코드가 포함되어 있지 않습니다. 앱은 Windows에 포함된 HID, SetupAPI 및 XInput 시스템 API와 설치된 .NET Framework를 호출합니다. 이 시스템 구성 요소 자체는 배포본에 복사하지 않습니다.

SteelSeries 장치의 공개 프로토콜 동작을 확인하는 과정에서 다음 오픈 소스 프로젝트를 참고했습니다.

- HeadsetControl — <https://github.com/Sapd/HeadsetControl> — GNU General Public License v3.0

이 앱은 HeadsetControl의 소스나 바이너리를 포함하거나 링크하지 않으며, 현재 장치에 필요한 최소 상태 조회를 별도 코드로 구현합니다. 향후 해당 프로젝트의 코드를 복사하거나 링크하는 변경이 생기면 배포 전에 GPL 의무와 고지 내용을 다시 검토해야 합니다.

상호 운용성 확인에는 다음 공개 자료도 참고했습니다. 이 자료의 코드나 바이너리는 배포본에 포함하지 않습니다.

- Microsoft XInput 배터리 API 문서 — <https://learn.microsoft.com/windows/win32/api/xinput/nf-xinput-xinputgetbatteryinformation>
- ATK V HUB 안내 — <https://www.atk.store/blogs/news/atk-v-hub-instructions>
- AULA F108 Pro 공식 드라이버 페이지 — <https://aulagear.com/blogs/software/aula-f108-pro-driver>
- mouse_tray의 VXE 프로토콜 구현 — <https://github.com/Fan4Metal/mouse_tray>

SteelSeries, Arctis, AULA, VXE, Microsoft, Xbox 및 기타 제품명과 상표는 각 권리자의 자산입니다. 이 프로젝트는 해당 제조사의 공식 제품이거나 보증을 받은 도구가 아닙니다.
