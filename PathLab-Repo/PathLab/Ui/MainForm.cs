using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using PathLab.Algorithms.MaxPerpendicular06;
using PathLab.Algorithms.RadiusCandidates04;
using PathLab.Core;

namespace PathLab.Ui;

public sealed class MainForm : Form
{
    private static readonly Color Paper = Color.FromArgb(250, 250, 250);
    private static readonly Color Ink = Color.FromArgb(24, 24, 24);
    private static readonly Color Line = Color.FromArgb(222, 222, 222);
    private static readonly Color Accent = Color.FromArgb(37, 72, 167);

    private static readonly Color[] LayerColors =
    {
        Color.FromArgb(37, 72, 167),
        Color.FromArgb(178, 74, 30),
        Color.FromArgb(47, 107, 79),
        Color.FromArgb(94, 66, 132)
    };

    private static readonly string[] LayerColorNames = { "蓝", "橙", "绿", "紫" };

    private readonly GridCanvas _canvas = new();
    private readonly Label _singleAlgorithmLabel = new();
    private readonly FlowLayoutPanel _compareRow = new();
    private readonly ComboBox _compareAlgorithmABox = new();
    private readonly ComboBox _compareAlgorithmBBox = new();
    private readonly FlowLayoutPanel _referenceRow = new();
    private readonly Button _referencePickButton = new();
    private readonly Label _referenceHint = new();
    private readonly Label _referenceValueLabel = new();
    private readonly FlowLayoutPanel _candidateRow = new();
    private readonly TextBox _alphaInput = new();
    private readonly TextBox _minRadiusInput = new();
    private readonly TextBox _maxRadiusInput = new();
    private readonly Label _candidateValueLabel = new();
    private readonly CheckBox _candidateToggle = new();
    private readonly ComboBox _algorithmBox = new();
    private readonly Button _newMapButton = new();
    private readonly Button _runButton = new();
    private readonly Button _stepButton = new();
    private readonly Button _resetButton = new();
    private readonly Button _wallModeButton = new();
    private readonly Button _costModeButton = new();
    private readonly Button _clearModeButton = new();
    private readonly Button _portalModeButton = new();
    private readonly NumericUpDown _portalCost = new();
    private readonly Button _oneWayButton = new();
    private readonly ComboBox _oneWayDir = new();
    private readonly Button _launcherButton = new();
    private readonly ComboBox _launcherDir = new();
    private readonly NumericUpDown _launcherDistance = new();
    private readonly Button _tauntButton = new();
    private readonly Label _editHint = new();
    private readonly NumericUpDown _brushCost = new();
    private readonly CheckBox _rmgToggle = new();
    private readonly CheckBox _compareToggle = new();
    private readonly Label _status = new();
    private readonly TrackBar _speed = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly List<Robot> _robots = new();

    private GridMap? _map;
    private int _mapSeed;
    private string _mapSource = "内置";
    private int _compareIndexA;
    private int _compareIndexB = 1;
    private bool _updatingCompareSelection;
    private Cell? _referenceCell;
    private bool _referencePickedByUser;
    private readonly List<Cell> _referenceCells = new();
    private readonly PortalFacility _portals = new();
    private readonly OneWayFacility _oneWay = new();
    private readonly DirectionalLauncherFacility _launchers = new();
    private readonly TauntPointFacility _taunts = new();
    private IPathProbe? _activeProbe;
    private TauntPlan _activeTaunt = TauntPlan.None;

    public MainForm()
    {
        Text = "PathLab · 寻路可视化";
        ClientSize = new Size(1000, 660);
        MinimumSize = new Size(980, 580);
        BackColor = Paper;
        Font = new Font("Microsoft YaHei UI", 9f);
        DoubleBuffered = true;

        BuildUi();
        PopulateAlgorithms();

        _timer.Interval = IntervalForSpeed(_speed.Value);
        _timer.Tick += (_, _) => TickAnimation();
        ApplyMode(PaintMode.Wall);
        LoadNewMap(20261025);
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = Paper
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        Controls.Add(root);

        // ---- row 0: generation, algorithm, playback ----
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16, 10, 8, 4),
            BackColor = Paper,
            Margin = new Padding(0)
        };

        _newMapButton.Text = "新地图";
        _newMapButton.Width = 78;
        ConfigureToolButton(_newMapButton);
        _newMapButton.Click += (_, _) => LoadNewMap();
        top.Controls.Add(_newMapButton);

        _rmgToggle.Text = "RMG 生成";
        _rmgToggle.AutoSize = true;
        _rmgToggle.ForeColor = Ink;
        _rmgToggle.Checked = true;
        _rmgToggle.Margin = new Padding(0, 7, 10, 0);
        top.Controls.Add(_rmgToggle);

        _compareToggle.Text = "对比运行";
        _compareToggle.AutoSize = true;
        _compareToggle.ForeColor = Ink;
        _compareToggle.Margin = new Padding(0, 7, 14, 0);
        _compareToggle.CheckedChanged += (_, _) => CompareChanged();
        top.Controls.Add(_compareToggle);

        _candidateToggle.Text = "半径候选";
        _candidateToggle.AutoSize = true;
        _candidateToggle.ForeColor = Ink;
        _candidateToggle.Margin = new Padding(0, 7, 14, 0);
        _candidateToggle.CheckedChanged += (_, _) => CandidateModeChanged();
        top.Controls.Add(_candidateToggle);

        _singleAlgorithmLabel.Text = "算法";
        _singleAlgorithmLabel.AutoSize = true;
        _singleAlgorithmLabel.ForeColor = Ink;
        _singleAlgorithmLabel.Margin = new Padding(0, 7, 8, 0);
        top.Controls.Add(_singleAlgorithmLabel);
        _algorithmBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _algorithmBox.Width = 200;
        _algorithmBox.Margin = new Padding(0, 4, 14, 0);
        top.Controls.Add(_algorithmBox);

        _runButton.Text = "运行";
        _runButton.Width = 62;
        ConfigureToolButton(_runButton);
        _runButton.Click += (_, _) => ToggleRun();
        top.Controls.Add(_runButton);

        _stepButton.Text = "单步";
        _stepButton.Width = 56;
        ConfigureToolButton(_stepButton);
        _stepButton.Click += (_, _) => StepOnce();
        top.Controls.Add(_stepButton);

        _resetButton.Text = "重置";
        _resetButton.Width = 56;
        ConfigureToolButton(_resetButton);
        _resetButton.Click += (_, _) => ResetRobots();
        top.Controls.Add(_resetButton);

        top.Controls.Add(PlainLabel("速度"));
        _speed.Minimum = 1;
        _speed.Maximum = 20;
        _speed.Value = 12;
        _speed.TickStyle = TickStyle.None;
        _speed.AutoSize = false;
        _speed.Width = 130;
        _speed.Height = 26;
        _speed.Margin = new Padding(0, 3, 0, 0);
        _speed.ValueChanged += (_, _) => _timer.Interval = IntervalForSpeed(_speed.Value);
        top.Controls.Add(_speed);

        root.Controls.Add(top, 0, 0);

        // ---- row 1: compare A/B selection (visible only while comparing) ----
        BuildCompareRow();
        root.Controls.Add(_compareRow, 0, 1);

        // ---- row 2: reference point (visible only for reference-driven algorithms) ----
        BuildReferenceRow();
        root.Controls.Add(_referenceRow, 0, 2);

        // ---- row 3: radius candidates (visible only while the preview toggle is on) ----
        BuildCandidateRow();
        root.Controls.Add(_candidateRow, 0, 3);

        // ---- row 4: map editing ----
        var edit = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16, 8, 8, 4),
            BackColor = Paper,
            Margin = new Padding(0)
        };

        edit.Controls.Add(PlainLabel("编辑"));

        _wallModeButton.Text = "障碍";
        _wallModeButton.Width = 56;
        ConfigureToolButton(_wallModeButton);
        _wallModeButton.Click += (_, _) => ApplyMode(PaintMode.Wall);
        edit.Controls.Add(_wallModeButton);

        _costModeButton.Text = "代价";
        _costModeButton.Width = 56;
        ConfigureToolButton(_costModeButton);
        _costModeButton.Click += (_, _) => ApplyMode(PaintMode.Cost);
        edit.Controls.Add(_costModeButton);

        _clearModeButton.Text = "擦除";
        _clearModeButton.Width = 56;
        ConfigureToolButton(_clearModeButton);
        _clearModeButton.Click += (_, _) => ApplyMode(PaintMode.Clear);
        edit.Controls.Add(_clearModeButton);

        _portalModeButton.Text = "传送门";
        _portalModeButton.Width = 70;
        ConfigureToolButton(_portalModeButton);
        _portalModeButton.Click += (_, _) => ApplyMode(PaintMode.Portal);
        edit.Controls.Add(_portalModeButton);

        edit.Controls.Add(PlainLabel("传送代价"));
        _portalCost.Minimum = 0;
        _portalCost.Maximum = GridMap.MaxCost;
        _portalCost.Value = 2;
        _portalCost.Width = 52;
        _portalCost.Margin = new Padding(0, 4, 12, 0);
        _portalCost.ValueChanged += (_, _) =>
        {
            _portals.TeleportCost = (int)_portalCost.Value;
            OnMapEdited(this, EventArgs.Empty);
        };
        edit.Controls.Add(_portalCost);

        _oneWayButton.Text = "单向";
        _oneWayButton.Width = 56;
        ConfigureToolButton(_oneWayButton);
        _oneWayButton.Click += (_, _) => ApplyMode(PaintMode.OneWay);
        edit.Controls.Add(_oneWayButton);

        edit.Controls.Add(PlainLabel("箭头"));
        _oneWayDir.DropDownStyle = ComboBoxStyle.DropDownList;
        _oneWayDir.Items.AddRange(new object[] { "↑ 上", "↓ 下", "← 左", "→ 右" });
        _oneWayDir.SelectedIndex = 3;
        _oneWayDir.Width = 62;
        _oneWayDir.Margin = new Padding(0, 4, 12, 0);
        edit.Controls.Add(_oneWayDir);

        _launcherButton.Text = "跳板";
        _launcherButton.Width = 56;
        ConfigureToolButton(_launcherButton);
        _launcherButton.Click += (_, _) => ApplyMode(PaintMode.Launcher);
        edit.Controls.Add(_launcherButton);

        edit.Controls.Add(PlainLabel("方向"));
        _launcherDir.DropDownStyle = ComboBoxStyle.DropDownList;
        _launcherDir.Items.AddRange(new object[] { "↑ 上", "↓ 下", "← 左", "→ 右" });
        _launcherDir.SelectedIndex = 3;
        _launcherDir.Width = 62;
        _launcherDir.Margin = new Padding(0, 4, 12, 0);
        edit.Controls.Add(_launcherDir);

        edit.Controls.Add(PlainLabel("距离"));
        _launcherDistance.Minimum = 1;
        _launcherDistance.Maximum = 15;
        _launcherDistance.Value = 3;
        _launcherDistance.Width = 48;
        _launcherDistance.Margin = new Padding(0, 4, 12, 0);
        edit.Controls.Add(_launcherDistance);

        _tauntButton.Text = "嘲讽点";
        _tauntButton.Width = 70;
        ConfigureToolButton(_tauntButton);
        _tauntButton.Click += (_, _) => ApplyMode(PaintMode.Taunt);
        edit.Controls.Add(_tauntButton);

        edit.Controls.Add(PlainLabel("代价值"));
        _brushCost.Minimum = GridMap.MinCost;
        _brushCost.Maximum = GridMap.MaxCost;
        _brushCost.Value = 3;
        _brushCost.Width = 52;
        _brushCost.Margin = new Padding(0, 4, 12, 0);
        _brushCost.ValueChanged += (_, _) => _canvas.BrushCost = (int)_brushCost.Value;
        edit.Controls.Add(_brushCost);

        var applyCostButton = new Button { Text = "应用空地代价", Width = 116 };
        ConfigureToolButton(applyCostButton);
        applyCostButton.Click += (_, _) => ApplyEmptyCost();
        edit.Controls.Add(applyCostButton);

        _editHint.Text = "左键绘制 · 右键擦除";
        _editHint.AutoSize = true;
        _editHint.ForeColor = Color.FromArgb(150, 150, 150);
        _editHint.Margin = new Padding(8, 7, 0, 0);
        edit.Controls.Add(_editHint);

        root.Controls.Add(edit, 0, 4);

        // ---- row 5: canvas ----
        _canvas.Dock = DockStyle.Fill;
        _canvas.Margin = new Padding(0);
        _canvas.BrushCost = (int)_brushCost.Value;
        _canvas.MapEdited += OnMapEdited;
        _canvas.ReferencePicked += OnReferencePicked;
        _canvas.PortalEdited += OnPortalEdited;
        _canvas.OneWayEdited += OnOneWayEdited;
        _canvas.LauncherEdited += OnLauncherEdited;
        _canvas.TauntEdited += OnTauntEdited;
        root.Controls.Add(_canvas, 0, 5);

        // ---- row 6: status ----
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(16, 0, 0, 0);
        _status.ForeColor = Color.FromArgb(96, 96, 96);
        _status.Margin = new Padding(0);
        root.Controls.Add(_status, 0, 6);
    }

    private void CandidateModeChanged()
    {
        _candidateRow.Visible = _candidateToggle.Checked;
        RefreshCandidates();
    }

    /// <summary>Recomputes the radius candidate set and marks it on the canvas.</summary>
    private void RefreshCandidates()
    {
        // The marks are a preview only: keep the canvas clean while the toggle is off.
        if (!_candidateToggle.Checked)
        {
            _canvas.CandidateCells = Array.Empty<Cell>();
            _candidateValueLabel.Text = "未计算";
            return;
        }

        IReadOnlyList<Cell> cells = Array.Empty<Cell>();
        string text;

        if (_map is null)
        {
            text = "无地图";
        }
        else if (!double.TryParse(_alphaInput.Text.Trim(), NumberStyles.Float,
                     CultureInfo.InvariantCulture, out double alpha)
                 || double.IsNaN(alpha) || double.IsInfinity(alpha) || alpha < 0)
        {
            text = "比例无效";
        }
        else if (!int.TryParse(_minRadiusInput.Text.Trim(), NumberStyles.Integer,
                     CultureInfo.InvariantCulture, out int low) && _minRadiusInput.Text.Trim().Length > 0)
        {
            text = "下界须为非负整数";
        }
        else if (!int.TryParse(_maxRadiusInput.Text.Trim(), NumberStyles.Integer,
                     CultureInfo.InvariantCulture, out int high) && _maxRadiusInput.Text.Trim().Length > 0)
        {
            text = "上界须为非负整数";
        }
        else
        {
            int? min = _minRadiusInput.Text.Trim().Length == 0 ? null : low;
            int? max = _maxRadiusInput.Text.Trim().Length == 0 ? null : high;

            CandidateTargetsResult result =
                new RadiusCandidateFinder().Find(_map, _map.Goal, alpha, min, max);

            text = result.Success
                ? $"L={result.BasePathSteps} · R={result.Radius} · "
                  + $"区间 {result.MinRadius}–{result.MaxRadius} · 候选 {result.CandidateCount}"
                : $"失败（{result.Note}）";

            if (result.Success) cells = result.Candidates;
        }

        _candidateValueLabel.Text = text;
        _canvas.CandidateCells = cells;
    }

    private void BuildCandidateRow()
    {
        _candidateRow.Dock = DockStyle.Fill;
        _candidateRow.FlowDirection = FlowDirection.LeftToRight;
        _candidateRow.WrapContents = false;
        _candidateRow.AutoSize = true;
        _candidateRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _candidateRow.Padding = new Padding(16, 6, 8, 2);
        _candidateRow.BackColor = Paper;
        _candidateRow.Margin = new Padding(0);
        _candidateRow.Visible = false;

        _candidateRow.Controls.Add(PlainLabel("目标候选"));
        _candidateRow.Controls.Add(PlainLabel("比例"));

        _alphaInput.Text = RadiusCandidateFinder.InitialAlpha.ToString(CultureInfo.InvariantCulture);
        _alphaInput.Width = 52;
        _alphaInput.Margin = new Padding(0, 4, 12, 0);
        _alphaInput.TextChanged += (_, _) => RefreshCandidates();
        _candidateRow.Controls.Add(_alphaInput);

        _candidateRow.Controls.Add(PlainLabel("半径"));

        _minRadiusInput.Width = 40;
        _minRadiusInput.Margin = new Padding(0, 4, 4, 0);
        _minRadiusInput.TextChanged += (_, _) => RefreshCandidates();
        _candidateRow.Controls.Add(_minRadiusInput);

        Label rangeDash = PlainLabel("–");
        rangeDash.Margin = new Padding(3, 6, 3, 0);
        _candidateRow.Controls.Add(rangeDash);

        _maxRadiusInput.Width = 40;
        _maxRadiusInput.Margin = new Padding(0, 4, 12, 0);
        _maxRadiusInput.TextChanged += (_, _) => RefreshCandidates();
        _candidateRow.Controls.Add(_maxRadiusInput);

        _candidateValueLabel.AutoSize = true;
        _candidateValueLabel.ForeColor = Accent;
        _candidateValueLabel.Margin = new Padding(0, 6, 14, 0);
        _candidateValueLabel.Text = "未计算";
        _candidateRow.Controls.Add(_candidateValueLabel);

        Label hint = PlainLabel("半径留空 = 取 R；两端相同为圆周，不同为圆环；只算可行走且可达的格子");
        hint.ForeColor = Color.FromArgb(150, 150, 150);
        hint.Margin = new Padding(8, 7, 0, 0);
        _candidateRow.Controls.Add(hint);
    }

    private void BuildReferenceRow()
    {
        _referenceRow.Dock = DockStyle.Fill;
        _referenceRow.FlowDirection = FlowDirection.LeftToRight;
        _referenceRow.WrapContents = false;
        _referenceRow.AutoSize = true;
        _referenceRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _referenceRow.Padding = new Padding(16, 6, 8, 2);
        _referenceRow.BackColor = Paper;
        _referenceRow.Margin = new Padding(0);
        _referenceRow.Visible = false;

        _referenceRow.Controls.Add(PlainLabel("参考点"));

        _referencePickButton.Text = "选参考点";
        _referencePickButton.Width = 96;
        ConfigureToolButton(_referencePickButton);
        _referencePickButton.Click += (_, _) => ToggleReferencePicking();
        _referenceRow.Controls.Add(_referencePickButton);

        _referenceValueLabel.AutoSize = true;
        _referenceValueLabel.ForeColor = Accent;
        _referenceValueLabel.Margin = new Padding(0, 6, 14, 0);
        _referenceValueLabel.Text = "未设置";
        _referenceRow.Controls.Add(_referenceValueLabel);

        _referenceHint.AutoSize = true;
        _referenceHint.ForeColor = Color.FromArgb(150, 150, 150);
        _referenceHint.Margin = new Padding(8, 7, 0, 0);
        _referenceHint.Text = "点格子设参考点(空地或障碍) · 实线去程(最小代价) · 虚线返程(最短路程)";
        _referenceRow.Controls.Add(_referenceHint);
    }

    private void BuildCompareRow()
    {
        _compareRow.Dock = DockStyle.Fill;
        _compareRow.FlowDirection = FlowDirection.LeftToRight;
        _compareRow.WrapContents = false;
        _compareRow.AutoSize = true;
        _compareRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _compareRow.Padding = new Padding(16, 6, 8, 2);
        _compareRow.BackColor = Paper;
        _compareRow.Margin = new Padding(0);
        _compareRow.Visible = false;

        _compareRow.Controls.Add(PlainLabel("对比"));

        _compareRow.Controls.Add(ColoredLabel("蓝 A", LayerColors[0]));
        ConfigureCompareBox(_compareAlgorithmABox);
        _compareRow.Controls.Add(_compareAlgorithmABox);

        _compareRow.Controls.Add(ColoredLabel("橙 B", LayerColors[1]));
        ConfigureCompareBox(_compareAlgorithmBBox);
        _compareRow.Controls.Add(_compareAlgorithmBBox);

        Label hint = PlainLabel("同一张地图 · 同时规划 · 同时播放");
        hint.ForeColor = Color.FromArgb(150, 150, 150);
        hint.Margin = new Padding(8, 7, 0, 0);
        _compareRow.Controls.Add(hint);
    }

    private static void ConfigureCompareBox(ComboBox box)
    {
        box.DropDownStyle = ComboBoxStyle.DropDownList;
        box.Width = 200;
        box.Margin = new Padding(0, 3, 14, 0);
    }

    private static Label ColoredLabel(string text, Color color) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = color,
        Margin = new Padding(0, 6, 8, 0)
    };

    private void PopulateAlgorithms()
    {
        var algorithms = AlgorithmHost.All;
        foreach (IPathfinder algorithm in algorithms)
        {
            _algorithmBox.Items.Add(new AlgorithmItem(algorithm));
            _compareAlgorithmABox.Items.Add(new AlgorithmItem(algorithm));
            _compareAlgorithmBBox.Items.Add(new AlgorithmItem(algorithm));
        }

        if (algorithms.Count > 0)
        {
            _algorithmBox.SelectedIndex = 0;

            _compareAlgorithmABox.SelectedIndex = 0;
            _compareIndexA = 0;

            _compareAlgorithmBBox.SelectedIndex = algorithms.Count > 1 ? 1 : 0;
            _compareIndexB = _compareAlgorithmBBox.SelectedIndex;
        }
        else
        {
            foreach (ComboBox box in new[] { _algorithmBox, _compareAlgorithmABox, _compareAlgorithmBBox })
            {
                box.Items.Add("未发现算法");
                box.SelectedIndex = 0;
                box.Enabled = false;
            }

            _runButton.Enabled = false;
            _stepButton.Enabled = false;
        }

        // Comparing needs at least two modules.
        _compareToggle.Enabled = algorithms.Count >= 2;

        _algorithmBox.SelectedIndexChanged += AlgorithmChanged;
        _compareAlgorithmABox.SelectedIndexChanged += (_, _) => ApplyCompareSelection(true);
        _compareAlgorithmBBox.SelectedIndexChanged += (_, _) => ApplyCompareSelection(false);
    }

    private void LoadNewMap(int? fixedSeed = null)
    {
        StopAnimation();

        int seed = fixedSeed ?? Random.Shared.Next(1, int.MaxValue);
        GridMap map;
        string source;

        if (_rmgToggle.Checked && GridMapFactory.TryCreateFromRmg(seed, out GridMap rmgMap, out _))
        {
            map = rmgMap;
            source = "RMG";
        }
        else
        {
            map = GridMapFactory.CreateRandom(16, 16, seed);
            source = _rmgToggle.Checked ? "内置·RMG回退" : "内置";
        }

        _map = map;
        _mapSeed = seed;
        _mapSource = source;
        _referenceCell = null;
        _referencePickedByUser = false;
        _referenceCells.Clear();
        _portals.Reset();
        _oneWay.Reset();
        _launchers.Reset();
        _taunts.Reset();

        BuildRobots();
        EnsureReferenceDefault();
        PlanAll();
        _canvas.Bind(map, BuildLayers());
        SyncReferenceChrome();
        RefreshCandidates();
        UpdateStatus();
    }

    private void CompareChanged()
    {
        StopAnimation();

        bool comparing = _compareToggle.Checked;
        _singleAlgorithmLabel.Visible = !comparing;
        _algorithmBox.Visible = !comparing;
        _algorithmBox.Enabled = AlgorithmHost.All.Count > 0;
        _compareRow.Visible = comparing;

        BuildRobots();
        EnsureReferenceDefault();
        PlanAll();
        SyncView();
        UpdateStatus();
    }

    /// <summary>
    /// Keeps the two compare slots distinct: picking the same module in one slot
    /// moves the other slot to the module it held before.
    /// </summary>
    private void ApplyCompareSelection(bool pickedA)
    {
        if (_updatingCompareSelection) return;

        int a = _compareAlgorithmABox.SelectedIndex;
        int b = _compareAlgorithmBBox.SelectedIndex;
        int total = _compareAlgorithmABox.Items.Count;
        if (a < 0 || b < 0 || total < 2) return;

        if (a == b)
        {
            _updatingCompareSelection = true;

            int fallback = pickedA
                ? (_compareIndexB != a ? _compareIndexB : FirstOtherIndex(a, total))
                : (_compareIndexA != b ? _compareIndexA : FirstOtherIndex(b, total));

            if (pickedA) _compareAlgorithmBBox.SelectedIndex = fallback;
            else _compareAlgorithmABox.SelectedIndex = fallback;

            _updatingCompareSelection = false;
        }

        _compareIndexA = _compareAlgorithmABox.SelectedIndex;
        _compareIndexB = _compareAlgorithmBBox.SelectedIndex;

        if (!_compareToggle.Checked) return;

        StopAnimation();
        BuildRobots();
        EnsureReferenceDefault();
        PlanAll();
        SyncView();
        UpdateStatus();
    }

    private static int FirstOtherIndex(int excluded, int total)
    {
        for (int index = 0; index < total; index++)
            if (index != excluded)
                return index;

        return excluded;
    }

    private void AlgorithmChanged(object? sender, EventArgs e)
    {
        if (_compareToggle.Checked) return;

        StopAnimation();
        BuildRobots();
        EnsureReferenceDefault();
        PlanAll();
        SyncView();
        UpdateStatus();
    }

    private void OnMapEdited(object? sender, EventArgs e)
    {
        StopAnimation();

        // Painting over a portal cell would make it walkable again; the facility closes it.
        if (_map is not null) _portals.CloseCells(_map);
        _canvas.PortalCells = _portals.Positions();

        PlanAll();
        SyncView();
        RefreshCandidates();
        UpdateStatus();
    }

    private void ToggleReferencePicking()
    {
        bool picking = !_canvas.PickReference;
        _canvas.PickReference = picking;
        _referencePickButton.Text = picking ? "点击格子…" : "选参考点";
        StylePickButton(picking);

        // One tool at a time: while a reference is being picked, no paint button looks active.
        HighlightModeButtons(picking ? null : _canvas.Mode);
    }

    private void OnReferencePicked(object? sender, Cell cell)
    {
        // Reference-set modules collect many cells: a click adds one, a second click
        // on the same cell removes it, and picking stays on until the button is pressed again.
        if (UsesReferenceSet())
        {
            if (_map is null || !_map.InBounds(cell)) return;

            StopAnimation();
            if (!_referenceCells.Remove(cell)) _referenceCells.Add(cell);
            ApplyReferencePoint();

            PlanAll();
            SyncView();
            UpdateStatus();
            return;
        }

        _canvas.PickReference = false;
        _referencePickButton.Text = "选参考点";
        StylePickButton(false);
        HighlightModeButtons(_canvas.Mode);

        // Walls are legal references too: the farthest search seeds from their open neighbours.
        if (_map is null || !_map.InBounds(cell)) return;
        if (_referenceCell == cell) return;

        StopAnimation();
        _referenceCell = cell;
        _referencePickedByUser = true;
        ApplyReferencePoint();

        PlanAll();
        SyncView();
        UpdateStatus();
    }

    /// <summary>True when a visible robot collects a whole set of reference cells.</summary>
    private bool UsesReferenceSet()
        => _robots.Any(robot => robot.Algorithm is IReferenceCellSetModule);

    /// <summary>
    /// First wall (row-major) when a module needs an obstacle, the entrance otherwise.
    /// Only applies before the user has picked a reference, so a manual pick is never
    /// silently replaced. Setup modules start with no references at all.
    /// </summary>
    private void EnsureReferenceDefault()
    {
        if (_referencePickedByUser || _map is null) return;
        if (!_robots.Any(robot => robot.Algorithm is IReferencePointModule)) return;

        bool needsObstacle = _robots.Any(robot => robot.Algorithm is IViaPointModule);
        Cell? fallback = needsObstacle
            ? ObstacleCluster.FirstWall(_map)
            : _map.Goal;

        if (fallback is not { } cell || _referenceCell == cell) return;

        _referenceCell = cell;
        ApplyReferencePoint();
    }

    /// <summary>Pushes the reference cells into every reference-driven module.</summary>
    private void ApplyReferencePoint()
    {
        Cell[] cells = _referenceCells.ToArray();

        foreach (Robot robot in _robots)
        {
            if (robot.Algorithm is IReferencePointModule module)
                module.ReferencePoint = _referenceCell;

            if (robot.Algorithm is IReferenceCellSetModule setModule)
                setModule.ReferenceCells = cells;
        }
    }

    private void SyncReferenceChrome()
    {
        bool singleMode = _robots.Any(robot => robot.Algorithm is IReferencePointModule);
        bool setMode = UsesReferenceSet();
        bool needed = singleMode || setMode;
        bool clusterMode = _robots.Any(robot => robot.Algorithm is IViaPointModule);
        bool scoreMode = _robots.Any(robot => robot.Algorithm.Objective == SearchObjective.Score);

        _referenceRow.Visible = needed;
        if (!needed)
        {
            _canvas.PickReference = false;
            _referencePickButton.Text = "选参考点";
            StylePickButton(false);
            HighlightModeButtons(_canvas.Mode);
        }

        _referenceHint.Text = setMode
            ? "点格子加入参考点(再点移出) · 参考点为软障碍：先少穿越、再最短"
            : clusterMode
                ? "点障碍设参考点(自动取整块障碍群) · 空心方框为周边格 · F 为本轮经过格"
                : scoreMode
                    ? "点格子设标记点 Q(空地或障碍) · 得分 = Σ 步数 × 该步所在直线到 Q 的垂直距离"
                    : "点格子设参考点(空地或障碍) · 实线去程(最小代价) · 虚线返程(最短路程)";

        _referenceValueLabel.Text = setMode
            ? $"{_referenceCells.Count} 个"
            : _referenceCell is { } cell ? $"({cell.X}, {cell.Y})" : "未设置";

        _canvas.ReferenceCell = singleMode ? _referenceCell : null;
        _canvas.ReferenceCells = setMode ? _referenceCells.ToArray() : Array.Empty<Cell>();
        _canvas.FarthestCell = needed ? FirstViaPoint() : null;
        _canvas.RingCells = clusterMode ? FirstRing() : Array.Empty<Cell>();
    }

    /// <summary>Marker cell of the last search: ring cell for 05, farthest cell for 03.</summary>
    private Cell? FirstViaPoint()
    {
        foreach (Robot robot in _robots)
        {
            if (robot.Algorithm is IViaPointModule { ViaCell: { } via }) return via;
            if (robot.Algorithm is IReferencePointModule { FarthestPoint: { } farthest }) return farthest;
        }

        return null;
    }

    private IReadOnlyList<Cell> FirstRing()
    {
        foreach (Robot robot in _robots)
            if (robot.Algorithm is IViaPointModule ringModule)
                return ringModule.RingCells;

        return Array.Empty<Cell>();
    }

    private void StylePickButton(bool active)
    {
        _referencePickButton.BackColor = active ? Accent : Color.White;
        _referencePickButton.ForeColor = active ? Color.White : Ink;
        _referencePickButton.FlatAppearance.BorderColor = active ? Accent : Line;
        _referencePickButton.FlatAppearance.MouseOverBackColor =
            active ? Accent : Color.FromArgb(243, 243, 243);
    }

    private void ApplyEmptyCost()
    {
        if (_map is null) return;
        _map.SetAllTraversableCost((int)_brushCost.Value);
        OnMapEdited(this, EventArgs.Empty);
    }

    private void ToggleRun()
    {
        if (_timer.Enabled)
        {
            StopAnimation();
            UpdateStatus();
            return;
        }

        if (!_robots.Any(robot => robot.HasPlan)) return;

        if (_robots.All(robot => !robot.HasPlan || robot.AtGoal))
        {
            ResetAll();
            SyncView();
        }

        _timer.Interval = IntervalForSpeed(_speed.Value);
        _timer.Start();
        _runButton.Text = "暂停";
        UpdateStatus();
    }

    private void StepOnce()
    {
        StopAnimation();
        if (!_robots.Any(robot => robot.HasPlan)) return;

        if (_robots.All(robot => !robot.HasPlan || robot.AtGoal))
            ResetAll();

        foreach (Robot robot in _robots)
            robot.Advance();

        SyncView();
        UpdateStatus();
    }

    private void ResetRobots()
    {
        StopAnimation();
        ResetAll();
        SyncView();
        UpdateStatus();
    }

    private void ResetAll()
    {
        foreach (Robot robot in _robots)
            robot.Reset();
    }

    private void TickAnimation()
    {
        if (_robots.Count == 0)
        {
            StopAnimation();
            return;
        }

        bool moved = false;
        foreach (Robot robot in _robots)
            if (robot.Advance())
                moved = true;

        if (!moved)
        {
            StopAnimation();
            UpdateStatus();
            return;
        }

        SyncView();
        UpdateStatus();
    }

    private void StopAnimation()
    {
        _timer.Stop();
        _runButton.Text = "运行";
    }

    private void BuildRobots()
    {
        _robots.Clear();
        if (_map is null) return;

        if (_compareToggle.Checked)
        {
            IPathfinder? first = SelectedAlgorithm(_compareAlgorithmABox);
            IPathfinder? second = SelectedAlgorithm(_compareAlgorithmBBox);

            if (first is not null)
                _robots.Add(new Robot(_map, AlgorithmHost.Create(first)));

            if (second is not null && !ReferenceEquals(first, second))
                _robots.Add(new Robot(_map, AlgorithmHost.Create(second)));
        }
        else if (SelectedAlgorithm(_algorithmBox) is { } selected)
        {
            _robots.Add(new Robot(_map, AlgorithmHost.Create(selected)));
        }

        ApplyReferencePoint();
        ApplyFacilities();
    }

    /// <summary>
    /// Hands the current map plus its facilities to every transition-aware module:
    /// portal jumps, one-way edge rules and launcher chains through one composed probe.
    /// </summary>
    private void ApplyFacilities()
    {
        if (_map is null) return;

        // One composed probe per robot: portals first, one-way rules on top,
        // launcher jumps last. No algorithm knows any of the three by name.
        _activeProbe = _launchers.ApplyTo(_oneWay.ApplyTo(_portals.CreateProbe(_map)), _map);

        foreach (Robot robot in _robots)
            if (robot.Algorithm is ITransitionAware aware)
                aware.Probe = _activeProbe;
    }

    private void PlanAll()
    {
        // The effective taunt set depends on the current map and probe, so it is rebuilt
        // for every planning pass: edits never reuse a stale requirement.
        if (_map is not null && _activeProbe is not null)
            _activeTaunt = TauntPlan.Build(_map, _activeProbe, _taunts.EnabledPoints());

        foreach (Robot robot in _robots)
        {
            if (robot.Algorithm is ITauntAware tauntAware)
                tauntAware.Taunt = _activeTaunt;

            robot.Plan();

            // Keep the launcher event chain next to the coordinates, so a runtime can
            // replay every forced pad instead of guessing from a long jump.
            if (_map is not null && _activeProbe is not null && _launchers.Launchers.Count > 0
                && robot.Result is { Found: true } result)
                LauncherPathPlan.Attach(_map, _activeProbe, result);
        }
    }

    private IReadOnlyList<PathLayer> BuildLayers()
    {
        var layers = new List<PathLayer>(_robots.Count);

        for (int index = 0; index < _robots.Count; index++)
        {
            Robot robot = _robots[index];
            layers.Add(new PathLayer(robot.Result, robot.Position, LayerColor(index), TurnIndex(robot)));
        }

        return layers;
    }

    /// <summary>Path index where the outbound leg meets the return leg, or -1.</summary>
    private static int TurnIndex(Robot robot)
    {
        if (robot.Algorithm is not IReferencePointModule module) return -1;
        if (module.FarthestPoint is not { } farthest) return -1;
        if (robot.Result is not { Found: true } result) return -1;

        return result.Path.IndexOf(farthest);
    }

    private void SyncView()
    {
        if (_map is null) return;
        _canvas.PortalCells = _portals.Positions();
        _canvas.Passages = _oneWay.Passages;
        _canvas.Launchers = _launchers.Launchers;
        _canvas.TauntCells = _taunts.Positions();
        _canvas.UpdateLayers(BuildLayers());
        SyncReferenceChrome();
    }

    private void ApplyMode(PaintMode mode)
    {
        // One tool at a time: switching paint modes always drops reference picking.
        _canvas.PickReference = false;
        _referencePickButton.Text = "选参考点";
        StylePickButton(false);

        _canvas.Mode = mode;
        HighlightModeButtons(mode);

        _editHint.Text = mode switch
        {
            PaintMode.Portal => PortalHint,
            PaintMode.OneWay => OneWayHint,
            PaintMode.Launcher => LauncherHint,
            PaintMode.Taunt => TauntHint,
            _ => "左键绘制 · 右键擦除"
        };
    }

    /// <summary>Lights exactly one paint button; <c>null</c> leaves all of them unlit.</summary>
    private void HighlightModeButtons(PaintMode? mode)
    {
        var buttons = new (Button Button, PaintMode Mode)[]
        {
            (_wallModeButton, PaintMode.Wall),
            (_costModeButton, PaintMode.Cost),
            (_clearModeButton, PaintMode.Clear),
            (_portalModeButton, PaintMode.Portal),
            (_oneWayButton, PaintMode.OneWay),
            (_launcherButton, PaintMode.Launcher),
            (_tauntButton, PaintMode.Taunt)
        };

        foreach ((Button button, PaintMode buttonMode) in buttons)
        {
            bool selected = mode is { } current && buttonMode == current;
            button.BackColor = selected ? Accent : Color.White;
            button.ForeColor = selected ? Color.White : Ink;
            button.FlatAppearance.BorderColor = selected ? Accent : Line;
            button.FlatAppearance.MouseOverBackColor = selected ? Accent : Color.FromArgb(243, 243, 243);
        }
    }

    private const string PortalHint = "左键放传送门 · 右键移除 · 门格不可通行";

    private const string OneWayHint = "左键放单向通道 · 右键移除 · 顺箭头通行";

    private const string LauncherHint = "左键放指向跳板 · 右键移除 · 站旁边可选跳板方向";

    private const string TauntHint = "左键放嘲讽点 · 右键移除 · 红色小方块 = 必经点";

    /// <summary>Adds or removes one portal, then re-plans every robot on the new topology.</summary>
    private void OnPortalEdited(object? sender, FacilityEdit edit)
    {
        if (_map is null) return;

        StopAnimation();

        if (edit.Erase)
        {
            _portals.RemoveAt(_map, edit.Cell);
        }
        else if (!_portals.Add(_map, edit.Cell, out string? note))
        {
            _editHint.Text = note ?? "无法放置传送门";
            UpdateStatus();
            return;
        }

        _editHint.Text = PortalHint;
        _canvas.PortalCells = _portals.Positions();
        OnMapEdited(this, EventArgs.Empty);
    }

    /// <summary>Adds or removes one one-way passage; the terrain itself never changes.</summary>
    private void OnOneWayEdited(object? sender, FacilityEdit edit)
    {
        if (_map is null) return;

        StopAnimation();

        if (edit.Erase)
        {
            _oneWay.RemoveAt(edit.Cell);
        }
        else
        {
            var direction = (Direction)_oneWayDir.SelectedIndex;
            if (!_oneWay.Add(_map, edit.Cell, direction, out string? note))
            {
                _editHint.Text = note ?? "无法放置单向通道";
                UpdateStatus();
                return;
            }
        }

        _editHint.Text = OneWayHint;
        _canvas.Passages = _oneWay.Passages;
        OnMapEdited(this, EventArgs.Empty);
    }

    /// <summary>Adds or removes one directional launcher; the terrain itself never changes.</summary>
    private void OnLauncherEdited(object? sender, FacilityEdit edit)
    {
        if (_map is null) return;

        StopAnimation();

        if (edit.Erase)
        {
            _launchers.RemoveAt(edit.Cell);
        }
        else
        {
            var direction = (Direction)_launcherDir.SelectedIndex;
            int distance = (int)_launcherDistance.Value;
            if (!_launchers.Add(_map, edit.Cell, direction, distance, out string? note))
            {
                _editHint.Text = note ?? "无法放置指向跳板";
                UpdateStatus();
                return;
            }
        }

        _editHint.Text = LauncherHint;
        _canvas.Launchers = _launchers.Launchers;
        OnMapEdited(this, EventArgs.Empty);
    }

    /// <summary>Adds or removes one taunt point; the terrain itself never changes.</summary>
    private void OnTauntEdited(object? sender, FacilityEdit edit)
    {
        if (_map is null) return;

        StopAnimation();

        if (edit.Erase)
        {
            _taunts.RemoveAt(edit.Cell);
        }
        else if (!_taunts.Add(_map, edit.Cell, out string? note))
        {
            _editHint.Text = note ?? "无法放置嘲讽点";
            UpdateStatus();
            return;
        }

        _editHint.Text = TauntHint;
        _canvas.TauntCells = _taunts.Positions();
        OnMapEdited(this, EventArgs.Empty);
    }

    private static IPathfinder? SelectedAlgorithm(ComboBox box)
    {
        if (box.SelectedItem is AlgorithmItem item) return item.Algorithm;
        return null;
    }

    private void UpdateStatus()
    {
        if (_map is null)
        {
            _status.Text = "无地图";
            return;
        }

        if (_robots.Count == 0)
        {
            _status.Text = $"无可用算法 · {_mapSource} · seed {_mapSeed}";
            return;
        }

        if (_robots.Count == 1)
        {
            Robot robot = _robots[0];
            SearchResult? result = robot.Result;
            string walked = robot.HasPlan ? $" · 已走 {robot.MovesDone}" : string.Empty;

            _status.Text = $"{robot.Algorithm.Name} · {FormatPath(robot)}"
                         + FormatReference(robot)
                         + TauntSupportNote(robot)
                         + $" · 扩展 {result?.Explored.Count ?? 0}"
                         + $" · {result?.ElapsedMs ?? 0:F1} ms · {_mapSource} · seed {_mapSeed}{walked}";
            return;
        }

        var parts = new List<string>(_robots.Count);
        for (int index = 0; index < _robots.Count; index++)
        {
            Robot robot = _robots[index];
            parts.Add($"{LayerColorName(index)} {robot.Algorithm.Name} {FormatShort(robot)}");
        }

        string moves = string.Join("/", _robots.Select(
            robot => robot.HasPlan ? robot.MovesDone.ToString() : "-"));

        string taunts = _activeTaunt.HasAny && _robots.Any(robot => robot.Algorithm is not ITauntAware)
            ? " · 部分算法不执行嘲讽点约束"
            : string.Empty;

        _status.Text = $"对比运行 · {string.Join(" · ", parts)}"
                     + $" · 已走 {moves}{taunts} · {_mapSource} · seed {_mapSeed}";
    }

    /// <summary>Warns when the selected module has no taunt constraint at all.</summary>
    private string TauntSupportNote(Robot robot)
        => _activeTaunt.HasAny && robot.Algorithm is not ITauntAware
            ? " · 该算法不执行嘲讽点约束"
            : string.Empty;

    private static string FormatPath(Robot robot)
    {
        SearchResult? result = robot.Result;
        if (result is null) return "未规划";
        if (!result.Found)
            return string.IsNullOrWhiteSpace(result.Note) ? "不可达" : $"不可达（{result.Note}）";

        int steps = result.Path.Count - 1;
        int special = PortalFacility.CountTeleports(result.Path);
        int launchers = result.LauncherJumps.Count;
        int teleports = Math.Max(0, special - launchers);
        string extras = (launchers > 0 ? $" · 跳板 {launchers} 次" : string.Empty)
                        + (teleports > 0 ? $" · 传送 {teleports} 次" : string.Empty)
                        + (result.TauntRequiredCount > 0
                            ? $" · 嘲讽点 {result.TauntVisitedCount}/{result.TauntRequiredCount}"
                            : string.Empty);

        // Reference-set modules report a crossing count instead of a score.
        if (robot.Algorithm is IReferenceCellSetModule)
            return $"{steps} 步 · 穿越 {result.ReferenceCount} · {result.TerminationReason}" + extras;

        // Score modules fill the termination reason; steps/price modules leave it null.
        if (result.TerminationReason is not null)
        {
            double average = MaxPerpendicularPathfinder.AverageEdgeWeight(
                (long)result.Score, steps);
            int turns = MaxPerpendicularPathfinder.TurnCount(result.Path);

            return $"{steps} 步 · 得分 {result.Score} · 平均 {average:F1} · 拐弯 {turns}"
                 + $" · {result.TerminationReason}" + extras;
        }

        return $"{steps} 步 · 代价 {result.Cost}" + extras;
    }

    private static string FormatReference(Robot robot)
    {
        if (robot.Algorithm is IReferenceCellSetModule setModule)
        {
            IReadOnlyList<Cell> used = setModule.UsedReferenceCells;
            return $" · 参考点 {used.Count} 个 · 穿越 {robot.Result?.ReferenceCount ?? 0}";
        }

        if (robot.Algorithm is not IReferencePointModule module) return string.Empty;
        if (module.UsedReference is not { } reference) return string.Empty;

        if (robot.Algorithm.Objective == SearchObjective.Score)
            return $" · 标记点 Q ({reference.X}, {reference.Y})";

        if (robot.Algorithm is IViaPointModule ringModule)
        {
            string via = ringModule.ViaCell is { } point
                ? $"经过 ({point.X}, {point.Y})"
                : "无经过格";

            return $" · 参考 ({reference.X}, {reference.Y}) · {via} · 周边 {ringModule.RingCells.Count} 格";
        }

        string farthest = module.FarthestPoint is { } far
            ? $"最远 ({far.X}, {far.Y})"
            : "无最远点";

        return $" · 参考 ({reference.X}, {reference.Y}) · {farthest}";
    }

    private static string FormatShort(Robot robot)
    {
        SearchResult? result = robot.Result;
        if (result is null) return "未规划";
        if (!result.Found) return "不可达";

        int steps = result.Path.Count - 1;

        if (robot.Algorithm is IReferenceCellSetModule)
            return $"{steps}步/{result.ReferenceCount}穿";

        return result.TerminationReason is not null
            ? $"{steps}步/{result.Score}分/{(result.IsOptimal ? "精确" : "未证")}"
            : $"{steps}步/{result.Cost}代价";
    }

    private static Color LayerColor(int index) => LayerColors[index % LayerColors.Length];

    private static string LayerColorName(int index) => LayerColorNames[index % LayerColorNames.Length];

    private static int IntervalForSpeed(int value) => Math.Max(40, 420 - value * 18);

    private static Label PlainLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Ink,
        Margin = new Padding(0, 7, 8, 0)
    };

    private static void ConfigureToolButton(Button button)
    {
        button.Height = 29;
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Color.White;
        button.ForeColor = Ink;
        button.Cursor = Cursors.Hand;
        button.Margin = new Padding(0, 0, 8, 0);
        button.FlatAppearance.BorderColor = Line;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(243, 243, 243);
    }

    private sealed class AlgorithmItem
    {
        public AlgorithmItem(IPathfinder algorithm) => Algorithm = algorithm;

        public IPathfinder Algorithm { get; }

        public override string ToString() => Algorithm.Name;
    }
}
