# Windows 화면 음성 녹음기
테스트 환경: window11

Windows에서 컴퓨터로 들리는 Google Meet 소리와 내 마이크를 함께 녹음해 하나의 MP3 파일로 저장하는 포터블 앱입니다. 화면은 녹화하지 않습니다.

## 
릴리즈 0.1.0 확인
https://github.com/xinun/simple_recording_tool/releases/tag/v0.1.0


## 사용 방법

1. `MeetRecorder.exe`를 실행합니다.
2. 회의 소리가 나오는 스피커 또는 헤드폰과 사용할 마이크를 선택합니다.
3. 마이크가 필요 없다면 `사용 안 함 (컴퓨터 소리만 녹음)`을 선택합니다.
4. 자동 입력된 오늘 날짜 회의 제목을 필요에 따라 수정합니다.
5. **녹음 시작**을 누릅니다.
6. 회의가 끝나면 **녹음 종료**를 누릅니다.
7. MP3 저장이 완료되면 저장 폴더가 열립니다.

기본 저장 위치는 `문서\Meet 녹음`입니다. 녹음 중에는 두 오디오 원본을 임시 WAV로 보관하고, 종료 시 하나의 128kbps MP3로 자동 결합한 뒤 임시 파일을 삭제합니다.

## 주의사항

- 녹음 시작 전에 Windows 설정에서 이 앱의 마이크 접근을 허용해야 합니다.
- Bluetooth 헤드셋은 통화 모드로 전환될 때 음질이 낮아질 수 있습니다.
- 녹음 중 스피커·헤드폰 또는 마이크 장치를 변경하지 마세요.
  - 장치 오류로 녹음이 중단되어도 **녹음 종료**를 누르면 수집된 내용을 MP3로 저장하고 경고를 표시합니다. 중단된 원본의 남은 구간은 무음이며, 새 장치로 자동 전환되지는 않습니다.
  - MP3 저장에 실패하면 복구할 수 있도록 임시 WAV를 보존하고 오류 창에 경로를 표시합니다.
- Windows에 기본 통신 장치가 지정되어 있지 않으면 목록의 첫 번째 활성 장치를 자동 선택합니다.
- 마이크가 없거나 필요하지 않으면 컴퓨터에서 재생되는 소리만 녹음할 수 있습니다.
- 회사 정책과 관련 법규를 확인하고 회의 참가자에게 녹음 사실을 알리세요.
- 첫 실행 시 서명되지 않은 개인 앱에 대한 Windows SmartScreen 경고가 나타날 수 있습니다.

## 개발 및 빌드

저장소 내부 `.dotnet`에 .NET 8 SDK가 준비되어 있다는 전제입니다.

```powershell
.\.dotnet\dotnet.exe restore
.\.dotnet\dotnet.exe build --configuration Release
.\publish.ps1
```

장치 중단 및 저장 회귀 검증: `.\.dotnet\dotnet.exe run --project tests/RecorderChecks.csproj --configuration Release`

포터블 실행 파일은 `dist\MeetRecorder.exe`에 생성됩니다.

사용 라이브러리:

- NAudio 2.3.0
- NAudio.Lame 2.1.0
