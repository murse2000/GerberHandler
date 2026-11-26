# GerberHandler

GerberHandler는 KiCad에서 내보낸 단일 PCB 제조 파일로 V-cut 패널을 만들고 결과를 2D와 3D로 검토하는 Windows x64 앱입니다.

## 설치와 사용

1. [GitHub Releases](https://github.com/murse2000/GerberHandler/releases/latest)에서 `GerberHandler-win-Setup.exe`를 내려받아 설치합니다.
2. KiCad에서 Gerber와 같은 원점의 mm 단위 소수점 Excellon을 내보냅니다. 도금·비도금 드릴은 파일을 분리합니다. PCB 외곽 Gerber를 포함한 폴더 또는 ZIP을 앱에서 엽니다. Gerber X2 속성과 KiCad의 주석형 레이어 역할을 지원합니다.
3. 행·열, 레일과 마크를 설정한 뒤 **패널 미리보기**를 누릅니다. 제조 ZIP을 만들 때는 사용할 제조·조립 조건을 프리셋에 등록하고 선택합니다.
4. **제조 ZIP 저장** 후 실제 출력이 재판독됩니다. 2D에서 레이어와 배열 셀을 비교하고 3D에서 패널과 드릴을 확인합니다. 수정할 작업은 `.ghpanel` 프로젝트로 저장합니다.

닫힌 직선·원호 외곽은 원본과 배열 미리보기에서 2D·3D로 볼 수 있습니다. V-cut 패널 생성은 동일 방향 직사각형 보드와 분리된 원형 드릴을 지원합니다. 제조용으로 쓰기 전에 제조사의 외곽·절단 데이터 표기 방식과 샘플 패널을 확인하세요.

행·열 간격을 입력하고 빈 PCB 스트립 또는 열린 공간을 선택할 수 있습니다. V-cut 제조 ZIP은 직사각형 보드와 빈 PCB 스트립에 적용하며, 스트립을 제조하려면 프리셋에 제조사의 최소 V-cut 간격을 등록하세요. Gerber에는 실장 부품의 몸체 치수가 없으므로 돌출량과 조립 여유를 확인해 간격을 입력하세요. 왼쪽 눈 아이콘으로 레이어를 전환하고 **설정 → 레이어 색상**에서 표시색을 지정합니다.

둥근 모서리 보드는 **탭 라우팅 + 마우스바이트**를 선택하세요. 가이드 레일은 각 방향에서 선택하고, 사용 중인 축의 보드 간격과 선택한 레일 쪽 가공 간격을 라우터 공구 지름보다 크게 설정합니다. 탭은 양쪽에서 맞닿는 직선 외곽에 자동 배치합니다. 타공은 탭 쪽에 배치하고 제조 ZIP의 NPTH 드릴에 기록합니다. 패널 외곽과 라우팅 개구는 `panel-Profile.gbr`에 함께 저장합니다. 제조사에 공구 지름, 레일 방향, 탭과 타공 조건 및 절단 후 남는 탭 자국을 확인하세요. 왼쪽의 **전체 표시**, **전체 숨기기**로 현재 레이어를 한 번에 전환할 수 있습니다.

제조·조립 조건 목록에서 **Eurocircuits 조립 · 공개 조건 참고**를 바로 선택할 수 있습니다. 프리셋 등록 화면에는 이 참고값을 자동으로 채우고, **현재 마크 치수 사용**으로 작업 중인 피두셜과 홀 치수를 가져올 수 있습니다. 최종 값은 주문할 업체의 사양에 맞춰 적용하세요. [패널 크기·레일·절단](https://www.eurocircuits.com/technical-guidelines/panel-guidelines/customer-panel-basics/) · [동박 이격](https://www.eurocircuits.com/copper-and-the-board-edge/) · [피두셜 치수](https://www.eurocircuits.com/tips-tricks/finding-fiducials/) · [고정 홀](https://www.eurocircuits.com/technical-guidelines/panel-guidelines/ec-panel-by-eurocircuits/)

**패널 미리보기**의 2D 탭에서 노란 원으로 표시된 피두셜을 끌면 기존 위치를 기준으로 X·Y가 각각 1 mm 단위로 이동합니다. 레일 밖이거나 다른 피처와 충돌하는 위치는 이전 좌표로 복원합니다. 자동 배치로 돌아가려면 피두셜 좌표 칸을 비우고 미리보기를 다시 누르세요.

## 업데이트

설치된 앱은 시작할 때 GitHub의 안정 버전을 확인합니다. **도움말 → 업데이트 확인**에서 직접 확인할 수도 있습니다. 최신 설치 버전과 새 버전, 해당 릴리스 변경 내용을 앱에서 확인한 뒤 설치를 승인할 수 있습니다. 열린 작업의 저장 또는 종료를 확인한 뒤 패키지를 내려받아 적용하고 앱을 재시작합니다.

## 빌드와 배포

Windows에서 .NET 10 SDK와 PowerShell 7.2 이상으로 빌드합니다. 앱 설치에는 별도 .NET 설치가 필요 없습니다.

```powershell
dotnet build -c Release
./Publish-Release.ps1 -Version 0.1.0 -NotesFile ./release-notes/0.1.0.md
```

패키징은 설치 파일, 업데이트 패키지, 업데이트 인덱스와 SHA-256 목록을 생성합니다. 새 버전을 배포할 때는 릴리스 노트를 만들고 소스와 해당 버전 태그를 GitHub에 푸시한 뒤 `-Publish`로 게시합니다.
