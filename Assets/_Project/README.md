# Morld 目前使用的內容

- 主場景：`Assets/Scenes/SampleScene.unity`（目前唯一啟用建置的場景）。
- 玩家出生物件：SampleScene 內的 `PlayerSpawnPoint`。
- 主玩家預製物件：`Assets/_Project/Prefabs/Player/MorldPlayer.prefab`。
- 執行時生成的玩家：`MorldPlayer(Clone)`。
- 玩家移動、跳躍、落地、繞行與下落：`Scripts/Gameplay/PlayerController.cs`。
- 按鍵顯示、Bug 標記與返回平台：`Scripts/Debug/PlayerDebugTools.cs`。
- 主關卡預製物件：`Prefabs/levels/Test Level/Test Level 1.prefab`。

`MorldPlayer` 是目前主場景使用的玩家。
鏡頭層級：玩家根物件保留 CharacterController、遊戲邏輯與 Visual；獨立子物件 `ViewPivot` 負責四向轉向，其下放 `FollowCamera`（Cinemachine 虛擬鏡頭）及 `Four-point detection`（視角相關遮擋採樣）。movementPlane、CameraRotationAbility.cameraPivot 與 FezWorldViewState.viewPivot 都指向 ViewPivot；虛擬鏡頭仍跟隨玩家根物件。轉向不再旋轉碰撞本體。場景中的 Main Camera 保持獨立，由 CinemachineBrain 輸出。
正交鏡頭安全距離：SampleScene 進入 Play／執行版後，Main Camera 自動加上 `MorldOrthographicCameraSafety`（已有則不重複）。在 Cinemachine 更新與混合完成後，沿鏡頭視線往外退，讓整個近裁切面與地圖實體碰撞器保持至少 10 單位（目前標準方塊 10 格）的間距；預設值與執行時下限皆為 10。包含畫面邊緣、薄 Plane 與 MeshCollider；忽略玩家、敵人、Trigger 及深度代理。保留前一個安全位置，避免快速往深處移動時直接越過薄牆。有障礙立即退開，離開障礙後依 Return Speed 回復；不改正交大小、角色投影大小與畫面平面位置，並延長遠裁切面補償退開距離。安全距離、邊緣餘量、恢復速度與地形 Layer 可在元件調整；若要持續保存參數，停止 Play 後在 Main Camera 手動加入同名元件並儲存場景。停用元件會解除事件並還原它仍持有的鏡頭修正。
執行版 FPS 紀錄：一般 Windows 建置載入 SampleScene 後自動顯示右上角 FPS 面板，不需手動掛元件，也不要求 Development Build。F10 記錄正常遊玩，保留現有幀率上限與垂直同步；Shift＋F10 才會暫時解除上限及同步，測試最大效能。兩種模式皆先暖機 2 秒、再記錄 30 秒，再按 F10 提前結束；結束／取消／停用／退出時還原原設定並保存已有樣本。F11 開啟結果資料夾。CSV 與摘要保存在 `Application.persistentDataPath/MorldPerformance`，Windows 通常是 `%USERPROFILE%/AppData/LocalLow/<CompanyName>/Morld/MorldPerformance`。摘要記錄模式、螢幕更新率、解析度、品質與 FPS；CSV 包含逐幀時間、實際取樣間隔、GC 各代回收次數及未聚焦狀態，供排查偶發頓挫。計時不受 timeScale 影響。正式建置若移除 Profiler 區段，其欄位為 -1，不影響逐幀 FPS。記錄期間只在記憶體累積，完成後才寫檔；編輯器仍使用原本的量測視窗。
原型 120 FPS 模式：載入 SampleScene 後自動建立 `Morld Prototype Performance`，取消垂直同步並將正常遊玩上限設為 120；陰影仍保留，距離最多 35、解析度最多 Medium、四段級聯減為兩段，不提高原本較低的設定，不改動抗鋸齒、模型、碰撞與移動規則。這是目前原型的效能預設，也會作用於此場景的遊戲建置，並不保證硬體已能穩定跑滿 120。效能量測視窗可關閉模式，立即還原啟用前的設定；停止 Play／停用元件也還原，不改寫 QualitySettings 資產。
量測開始時暫時解除 FPS 上限，結束後恢復原上限；保留當時垂直同步設定。摘要會記錄模式、解析度、顯示卡、同步、陰影設定與未達 120 FPS 的影格比例。CSV 另包含相機渲染、腳本 Update、物理、GUI、編輯器迴圈及輸入 HUD／紀錄的區段，缺少的計數器填 -1，各區段可能重疊，不能相加。
測試 HUD 只在 Repaint 繪製，字串、歷史列與排版尺寸於內容改變時更新；手把辨識只在裝置名稱變更時重算，按鍵與軸名稱重用。鍵盤／手把紀錄、F6 標記、F7 顯示與 F8 返回平台保留。
地形渲染最佳化：Play／遊戲載入場景後，`Morld Terrain Render Optimizer` 自動對有地形標記的普通 MeshRenderer 共用「完整頂點、索引、UV、法線、子網格與邊界都相同」的執行時網格副本，並使用開啟 GPU Instancing 的執行時材質副本。支援 ProBuilder 及日後替換的一般 3D 模型，保留各物件獨立 Transform、Renderer、Collider、材質外觀與可見性控制；不更動資產或原始碰撞網格。未開 Read/Write 的模型，若本來使用相同網格資產，也會共用；不需要修改匯入設定。角色、敵人、含 Animator 的物件及透明材質不處理。效能量測視窗可切換開關作同路線比較；停止 Play 自動還原。Graphics Settings 保留 Instancing shader 變體，讓遊戲建置也能使用執行時啟用的 Instancing。
效能量測：進入 Play，開啟 `Morld > Debug > 關卡效能量測（30 秒）` 並按開始，切回 Game 視窗操作複雜區域。暖機 2 秒後記錄 30 秒，或提前結束／停止 Play 儲存。CSV 與摘要保存在專案 `Temp/MorldPerformance`，包含平均 FPS、最低單幀 FPS、95% 影格耗時、慢幀、Game 聚焦狀態、移動／尋路／玩家可見度 CPU 區段、GC 配置與繪製數量。CPU 區段彼此可能包含，不可相加；-1 代表該計數器不可用。量測包含編輯器開銷，不能直接當成遊戲發行版效能。工具不保存或增加場景物件。
已抽取 `FezPrisonAreaFix_20261020` 的空中平台繞行篩選，保留目前玩家控制器及其餘修正。`PlayerController > Optimize Airborne Platform Routing` 預設開啟：空中只對實際深度路徑相交的平台側面嘗試繞行，上升時足夠越過頂面則交給自然跳躍與實體碰撞；背景牆、落地支撐和斜坡系統維持各自判定。可於 Play 中暫時關閉作相同路線的效能與手感比較，退出 Play 後會還原 Inspector 的暫時變更。
局部地形判定：`PlayerController > Use Local Terrain Queries` 預設開啟，`Local Terrain Range` 預設左右各 3 單位（目前標準方塊為 1 單位）。每個移動影格依當前視角重新篩選，範圍額外涵蓋玩家碰撞箱、支撐物位移及高速移動量；只有附近地形參與反覆的局部繞行、支撐恢復與斜坡選路。不限制深度與高度，大型牆以碰撞器範圍而非物件中心判斷。遠處碰撞器不會停用，完整相連牆邊界、旋轉遮擋及物理通道檢查仍保留全圖規則。可關閉開關對照同路線效能。
落地搜尋的物理粗篩為空或飽和時，玩家改用同影格的附近清單作完整回退取樣，避免每個移動子步重新掃描遠方平台；未提供附近清單的其他呼叫仍使用全圖回退。落地條件使用重用的回呼與本次腳底位置，避免反覆建立捕捉位置的暫存回呼。三格篩選開關關閉時恢復全圖回退，可作同路線對照。
舊場景與其他玩家方案集中在 `Legacy`，停用與恢復方式請看 `Legacy/README.md`。

地圖編輯器：`Morld > Map > Block Map Editor`（舊入口仍保留）。筆刷包含平台、遮片牆、邊界牆、斜面、玩家生成點與敵人生成點。所有地形筆刷均可用方向選單與左／右轉按鈕每次旋轉 90°，以格子中心旋轉，放置後仍佔原格；不攔截 Unity 的 Q／E 快捷鍵。斜面 Prefab 留空時自動建立可用 ProBuilder 編輯的實心斜面。右鍵刪除、中鍵吸取、Shift＋左鍵清除與 Undo／Redo 皆可使用。
玩家生成點筆刷會搬移目前場景已有的 `PlayerSpawner`，沒有才建立，並依玩家碰撞器腳底偏移貼到表面，避免重複生成玩家。敵人筆刷可指定種類、面向及導航區，放置後可在 Inspector 調整巡邏與個別參數。新生成點放在獨立的 `MorldSpawns` 下，不作為地形方塊匯入，也不取代同格地形；新地形則保留在 `FezMapRoot`。生成點以 Gizmo 顯示，不添加遊戲碰撞器。停止 Play 後才可繪製，修改地形後須重建敵人導航。

斜面方塊：停止 Play 後，使用 `GameObject > Morld > Ramp Block (ProBuilder)` 建立 1×1×1 實心楔形方塊，預設往本地 +X 升高。可用 ProBuilder 編輯頂點、延長斜坡，或繞 Y 軸旋轉 90 度改朝向；上坡角度請維持 45 度以內。元件 `FezRampBlock` 與 `FezOneWayPlatform` 必須保留，碰撞使用封閉 MeshCollider。
左右看得到斜度的視角依斜頂走上走下；沿坡向看的平頂視角取投影最高頂緣，視為普通平台。玩家上下坡會保持貼地，並保留實心碰撞、繞行、S＋空白鍵先繞前側再下落及 F8 返回平台規則。修改靜態地形後需重建敵人的導航區；敵人仍依真實 3D 斜面尋路。
主玩家 CharacterController 的 Slope Limit 為 45 度；舊的 0 度設定會拒絕斜面支撐。膠囊的支撐高度依有限坡面與頂緣計算，包含碰撞安全間距；坡腳、坡頂及連續斜面接點使用真實頂緣高度判定，避免被整塊包圍盒當成側牆繞行。
坡腳尚未接觸時只保留行走通道，真正踩上坡面後才切換支撐與深度。S＋空白鍵下落會記住剛離開的平台，直到落到其他地面前都不重新取得其投影支撐，避免被較低的斜面表面接回；實體碰撞始終保留。
斜面路口可用方向選路：W＋A／D（或左搖桿斜上）優先上坡，純水平優先同高度，S＋A／D（或左搖桿斜下）優先下坡。其他可走方向作為備選，所以只有一條斜面時仍可水平行走。`PlayerController > Ramp Direction Threshold` 預設 0.35，垂直分量另須至少達水平分量的一半，避免搖桿輕微偏移改選路線；同優先度保留目前支撐，再選較近深度。此規則用於貼地選路，空中落地與 S＋空白鍵下落維持原規則。
當坡向沿目前鏡頭深度、畫面看起來為平頂時，靠近坡腳按 W／上方向鍵／左搖桿往上，可沿真實坡面上坡；往下則下坡。放開會保留坡面上的實際位置，不吸附最高頂緣，坡頂停止向深度前進。此操作只在實際接近斜面、可行走角度內且通道未被阻擋時啟用，普通地面上的上下輸入不會自由移動深度；S＋空白仍是先繞前側下落。
已站在斜面上時，水平移動或沿目前坡向輸入會優先延續頂緣相接、朝向一致的斜面；接點比較相鄰坡面的共同膠囊支撐高度，近似同高時交給行進方向前方的坡面，避免繼續跟著舊坡面邊緣下沉。正面沿深度爬坡也會交接相鄰同向斜面，只有沒有接續坡面時才停在單塊坡頂。跨深度交接仍須通過視線與實體通道檢查，不跨越不相接的空隙。
不同深度的對向斜面若尖端在畫面上相接，靠近接點時優先取得上方坡面；直線切換會撞到坡面時，先移到支撐高度，再檢查並執行深度移動，不反覆切回下方。既有 S＋空白下落仍獨立處理。詳細移動診斷預設關閉，可在 `PlayerController > Enable Movement Diagnostics` 開啟；繞行失敗時的相連邊界改用一次收集與排序合併，避免重複全圖掃描。
相接斜坡的實際膠囊路徑通過檢查後，其餘同接點坡面不再以整塊包圍盒要求角色繞到房間 Plane 外側；同向不同坡度也能交接。真正被擋住時仍每步檢查直接通道，原地相同的昂貴側向繞行搜尋最多每 0.1 秒重試；退開或改變方向立即重新判定。此修正已以暫存場景匯出的碰撞形狀重現卡點，並驗證四視角、不同坡度／高度／深度的組合及封閉通道。
選單會在選取方塊的右側建立斜面；沒有選取方塊時使用 Scene 視窗中心並對齊整數座標，場景有 `FezMapRoot` 時會收在其下。可自行存成 Prefab 重複使用。表面取樣回歸檢查選單：`Morld > Tests > Run Ramp Surface Regression`；實際碰撞與四面旋轉仍需 Play 測試。
