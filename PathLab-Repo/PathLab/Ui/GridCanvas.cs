using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using PathLab.Core;

namespace PathLab.Ui;

public enum PaintMode
{
    Wall,
    Cost,
    Clear,

    /// <summary>Left click adds a portal, right click removes one; no terrain is painted.</summary>
    Portal,

    /// <summary>Left click adds a one-way passage, right click removes one; terrain is untouched.</summary>
    OneWay,

    /// <summary>Left click adds a directional launcher, right click removes one; terrain is untouched.</summary>
    Launcher,

    /// <summary>Left click adds a taunt point, right click removes one; terrain is untouched.</summary>
    Taunt
}

/// <summary>One facility request from the canvas: add on left drag, erase on right drag.</summary>
public readonly record struct FacilityEdit(Cell Cell, bool Erase);

/// <summary>
/// Minimal grid renderer: walls are solid, weighted cells are a light gray
/// with their price printed, explored cells are dots, each algorithm layer
/// draws its own path and robot. Left drag paints, right drag erases.
/// </summary>
public sealed class GridCanvas : Panel
{
    private static readonly Color Paper = Color.FromArgb(250, 250, 250);
    private static readonly Color Ink = Color.FromArgb(22, 22, 22);
    private static readonly Color GridLine = Color.FromArgb(232, 232, 232);

    private GridMap? _map;
    private IReadOnlyList<PathLayer> _layers = Array.Empty<PathLayer>();
    private Cell? _lastPainted;
    private Cell? _referenceCell;
    private Cell? _farthestCell;
    private IReadOnlyList<Cell> _candidateCells = Array.Empty<Cell>();
    private IReadOnlyList<Cell> _ringCells = Array.Empty<Cell>();
    private IReadOnlyList<Cell> _referenceCells = Array.Empty<Cell>();
    private IReadOnlyList<Cell> _portalCells = Array.Empty<Cell>();
    private IReadOnlyList<OneWayPassage> _passages = Array.Empty<OneWayPassage>();
    private IReadOnlyList<DirectionalLauncher> _launchers = Array.Empty<DirectionalLauncher>();
    private IReadOnlyList<Cell> _tauntCells = Array.Empty<Cell>();
    private bool _pickReference;

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public PaintMode Mode { get; set; } = PaintMode.Wall;

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int BrushCost { get; set; } = 3;

    /// <summary>Reference cell drawn as a small `R` badge, or null.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Cell? ReferenceCell
    {
        get => _referenceCell;
        set
        {
            _referenceCell = value;
            Invalidate();
        }
    }

    /// <summary>Reference-set cells, each drawn as a small `R` badge; empty by default.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<Cell> ReferenceCells
    {
        get => _referenceCells;
        set
        {
            _referenceCells = value;
            Invalidate();
        }
    }

    /// <summary>Portal cells, each drawn as a small `P` badge; empty by default.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<Cell> PortalCells
    {
        get => _portalCells;
        set
        {
            _portalCells = value;
            Invalidate();
        }
    }

    /// <summary>One-way passages, each drawn as an arrow; empty by default.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<OneWayPassage> Passages
    {
        get => _passages;
        set
        {
            _passages = value;
            Invalidate();
        }
    }

    /// <summary>Directional launchers, each drawn as an arrow with its transfer distance.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<DirectionalLauncher> Launchers
    {
        get => _launchers;
        set
        {
            _launchers = value;
            Invalidate();
        }
    }

    /// <summary>Taunt-point cells, each drawn as a small red square.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<Cell> TauntCells
    {
        get => _tauntCells;
        set
        {
            _tauntCells = value;
            Invalidate();
        }
    }

    /// <summary>Turn cell drawn as a small `F` badge, or null.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Cell? FarthestCell
    {
        get => _farthestCell;
        set
        {
            _farthestCell = value;
            Invalidate();
        }
    }

    /// <summary>Candidate target cells drawn as hollow squares; empty by default.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<Cell> CandidateCells
    {
        get => _candidateCells;
        set
        {
            _candidateCells = value;
            Invalidate();
        }
    }

    /// <summary>Ring cells of a constrained route, drawn like <see cref="CandidateCells"/>; empty by default.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<Cell> RingCells
    {
        get => _ringCells;
        set
        {
            _ringCells = value;
            Invalidate();
        }
    }

    /// <summary>When true the next left click reports a reference cell instead of painting.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool PickReference
    {
        get => _pickReference;
        set
        {
            _pickReference = value;
            Invalidate();
        }
    }

    public event EventHandler? MapEdited;

    public event EventHandler<Cell>? ReferencePicked;

    /// <summary>Raised while the portal paint mode is active.</summary>
    public event EventHandler<FacilityEdit>? PortalEdited;

    /// <summary>Raised while the one-way paint mode is active.</summary>
    public event EventHandler<FacilityEdit>? OneWayEdited;

    /// <summary>Raised while the launcher paint mode is active.</summary>
    public event EventHandler<FacilityEdit>? LauncherEdited;

    /// <summary>Raised while the taunt-point paint mode is active.</summary>
    public event EventHandler<FacilityEdit>? TauntEdited;

    public GridCanvas()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Paper;
        Cursor = Cursors.Cross;
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer, true);
    }

    public GridMap? Map => _map;

    public void Bind(GridMap map, IReadOnlyList<PathLayer> layers)
    {
        _map = map;
        _layers = layers;
        _lastPainted = null;
        Invalidate();
    }

    public void UpdateLayers(IReadOnlyList<PathLayer> layers)
    {
        _layers = layers;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_map is null) return;

        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Paper);

        (float size, float originX, float originY) = Geometry();
        var cells = new RectangleF[_map.Width * _map.Height];

        using var wallBrush = new SolidBrush(Ink);
        using var gridPen = new Pen(GridLine, 1f);

        for (int y = 0; y < _map.Height; y++)
        for (int x = 0; x < _map.Width; x++)
        {
            var cell = new Cell(x, y);
            var rect = new RectangleF(originX + x * size, originY + y * size, size, size);
            cells[y * _map.Width + x] = rect;

            int cost = _map.CostAt(cell);
            if (cost == GridMap.Wall)
            {
                graphics.FillRectangle(wallBrush, rect);
            }
            else if (cost > 1)
            {
                int shade = Math.Max(196, 246 - (cost - 1) * 7);
                using var brush = new SolidBrush(Color.FromArgb(shade, shade, shade));
                graphics.FillRectangle(brush, rect);
            }
        }

        // Grid lines.
        float right = originX + _map.Width * size;
        float bottom = originY + _map.Height * size;
        for (int x = 0; x <= _map.Width; x++)
            graphics.DrawLine(gridPen, originX + x * size, originY, originX + x * size, bottom);
        for (int y = 0; y <= _map.Height; y++)
            graphics.DrawLine(gridPen, originX, originY + y * size, right, originY + y * size);

        // Prices sit in the bottom-right corner so gate letters stay readable.
        using (var priceFont = new Font("Consolas", Math.Max(6f, size * 0.24f), FontStyle.Regular))
        using (var priceBrush = new SolidBrush(Color.FromArgb(110, 110, 110)))
        using (var corner = new StringFormat
        {
            Alignment = StringAlignment.Far,
            LineAlignment = StringAlignment.Far
        })
        {
            float inset = Math.Max(1.5f, size * 0.07f);

            for (int y = 0; y < _map.Height; y++)
            for (int x = 0; x < _map.Width; x++)
            {
                int cost = _map.CostAt(new Cell(x, y));
                if (cost <= 1) continue;

                RectangleF cellRect = cells[y * _map.Width + x];
                var cornerRect = new RectangleF(
                    cellRect.X + inset, cellRect.Y + inset,
                    cellRect.Width - inset * 2, cellRect.Height - inset * 2);

                graphics.DrawString(cost.ToString(), priceFont, priceBrush, cornerRect, corner);
            }
        }

        // Explored cells are shown only for a single algorithm:
        // two overlays of dots would bury the two paths.
        if (_layers.Count == 1 && _layers[0].Result is { } exploredResult)
        {
            float dot = Math.Max(1.2f, size * 0.09f);
            using var exploredBrush = new SolidBrush(Color.FromArgb(150, 150, 150));
            var drawn = new HashSet<Cell>();

            foreach (Cell cell in exploredResult.Explored)
            {
                // A node budget can report hundreds of thousands of visits; one dot per
                // cell is enough, and it keeps repainting cheap.
                if (!drawn.Add(cell)) continue;
                if (!_map.InBounds(cell) || _map.IsWall(cell)) continue;
                float cx = originX + (cell.X + 0.5f) * size;
                float cy = originY + (cell.Y + 0.5f) * size;
                graphics.FillEllipse(exploredBrush, cx - dot, cy - dot, dot * 2, dot * 2);
            }
        }

        // Paths: later layers are drawn thinner and on top, so an overlap shows
        // the upper colour as a core inside the lower colour.
        float pathWidth = Math.Max(2f, size * 0.15f);

        for (int index = 0; index < _layers.Count; index++)
        {
            PathLayer layer = _layers[index];
            if (layer.Result is not { Found: true } result || result.Path.Count == 0) continue;

            PointF[] points = result.Path
                .Select(c => new PointF(
                    originX + (c.X + 0.5f) * size,
                    originY + (c.Y + 0.5f) * size))
                .ToArray();

            float width = Math.Max(1.8f, pathWidth - index * size * 0.06f);
            using var pathPen = new Pen(layer.Color, width)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };

            bool jumps = HasJump(result.Path);

            if (points.Length == 1)
            {
                using var brush = new SolidBrush(layer.Color);
                float radius = Math.Max(2f, width);
                graphics.FillEllipse(brush, points[0].X - radius, points[0].Y - radius, radius * 2, radius * 2);
            }
            else if (jumps)
            {
                // A teleport is not a geometric segment: draw the walk in contiguous runs
                // and a dot for a lone cell, so no fake diagonal line crosses the map.
                foreach (PointF[] run in PathRuns(result.Path, points))
                {
                    if (run.Length > 1)
                    {
                        graphics.DrawLines(pathPen, run);
                        continue;
                    }

                    using var brush = new SolidBrush(layer.Color);
                    float radius = Math.Max(1.5f, width * 0.9f);
                    graphics.FillEllipse(brush,
                        run[0].X - radius, run[0].Y - radius, radius * 2, radius * 2);
                }
            }
            else if (layer.TurnIndex > 0 && layer.TurnIndex < points.Length - 1)
            {
                // Outbound leg solid, return leg dashed: the turn is the farthest cell.
                using var returnPen = new Pen(layer.Color, width)
                {
                    DashStyle = DashStyle.Dash,
                    LineJoin = LineJoin.Round
                };

                graphics.DrawLines(pathPen, points.Take(layer.TurnIndex + 1).ToArray());
                graphics.DrawLines(returnPen, points.Skip(layer.TurnIndex).ToArray());
            }
            else
            {
                graphics.DrawLines(pathPen, points);
            }
        }

        // Candidate / ring marks: hollow squares above the paths, so a preview stays readable.
        if (_candidateCells.Count > 0 || _ringCells.Count > 0)
        {
            float mark = Math.Max(3f, size * 0.46f);
            using var candidatePen = new Pen(Ink, Math.Max(1f, size * 0.035f));

            DrawHollow(graphics, _candidateCells, candidatePen, mark, size, originX, originY);
            DrawHollow(graphics, _ringCells, candidatePen, mark, size, originX, originY);
        }

        // Reference and turn badges sit under the robots, so a robot standing on
        // that cell stays visible while the badge ring still shows around it.
        DrawBadge(graphics, _referenceCell, "R", size, originX, originY);
        foreach (Cell cell in _referenceCells)
            DrawBadge(graphics, cell, "R", size, originX, originY);
        foreach (Cell cell in _portalCells)
            DrawBadge(graphics, cell, "P", size, originX, originY, square: true);
        DrawPassages(graphics, size, originX, originY);
        DrawLaunchers(graphics, size, originX, originY);
        DrawTaunts(graphics, size, originX, originY);
        DrawBadge(graphics, _farthestCell, "F", size, originX, originY);

        // Robots: offset sideways when several layers share a cell.
        float spread = size * 0.17f;
        float robotRadius = Math.Max(3f, size * 0.24f);

        for (int index = 0; index < _layers.Count; index++)
        {
            PathLayer layer = _layers[index];
            float offsetX = _layers.Count <= 1
                ? 0f
                : (index - (_layers.Count - 1) / 2f) * spread;

            float cx = originX + (layer.RobotCell.X + 0.5f) * size + offsetX;
            float cy = originY + (layer.RobotCell.Y + 0.5f) * size;

            using var halo = new SolidBrush(Paper);
            graphics.FillEllipse(halo, cx - robotRadius - 1.5f, cy - robotRadius - 1.5f,
                (robotRadius + 1.5f) * 2, (robotRadius + 1.5f) * 2);

            using var body = new SolidBrush(layer.Color);
            graphics.FillEllipse(body, cx - robotRadius, cy - robotRadius,
                robotRadius * 2, robotRadius * 2);
        }

        // Gate labels are drawn last so a robot cannot hide them:
        // the label inverts to paper when any robot stands on that gate.
        bool robotOnStart = _layers.Any(layer => layer.RobotCell == _map.Start);
        bool robotOnGoal = _layers.Any(layer => layer.RobotCell == _map.Goal);

        using (var gateFont = new Font("Consolas", Math.Max(8f, size * 0.34f), FontStyle.Bold))
        using (var inkBrush = new SolidBrush(Ink))
        using (var paperBrush = new SolidBrush(Paper))
        using (var centered = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        })
        {
            graphics.DrawString("S", gateFont,
                robotOnStart ? paperBrush : inkBrush,
                CellRect(_map.Start, size, originX, originY), centered);

            graphics.DrawString("E", gateFont,
                robotOnGoal ? paperBrush : inkBrush,
                CellRect(_map.Goal, size, originX, originY), centered);
        }

        if (_pickReference)
        {
            using var pickPen = new Pen(Color.FromArgb(37, 72, 167), 2f);
            graphics.DrawRectangle(pickPen, originX, originY, _map.Width * size, _map.Height * size);
        }
    }

    private void DrawHollow(Graphics graphics, IReadOnlyList<Cell> cells, Pen pen,
        float mark, float size, float originX, float originY)
    {
        if (_map is null) return;

        foreach (Cell cell in cells)
        {
            if (!_map.InBounds(cell)) continue;

            graphics.DrawRectangle(pen,
                originX + cell.X * size + (size - mark) / 2f,
                originY + cell.Y * size + (size - mark) / 2f,
                mark, mark);
        }
    }

    private static bool HasJump(IReadOnlyList<Cell> path)
    {
        for (int index = 1; index < path.Count; index++)
        {
            if (Math.Abs(path[index].X - path[index - 1].X)
                + Math.Abs(path[index].Y - path[index - 1].Y) != 1)
                return true;
        }

        return false;
    }

    private static IEnumerable<PointF[]> PathRuns(IReadOnlyList<Cell> path, PointF[] points)
    {
        int start = 0;

        for (int index = 1; index <= path.Count; index++)
        {
            bool cut = index == path.Count
                       || Math.Abs(path[index].X - path[index - 1].X)
                       + Math.Abs(path[index].Y - path[index - 1].Y) != 1;
            if (!cut) continue;

            yield return points[start..index];
            start = index;
        }
    }

    /// <summary>
    /// One-way passages are arrows, not badges: the shaft points from the tail
    /// to the head, so the forced direction reads at a glance next to the P/R marks.
    /// </summary>
    private void DrawPassages(Graphics graphics, float size, float originX, float originY)
    {
        if (_passages.Count == 0) return;

        Color arrow = Color.FromArgb(178, 74, 30);

        using var shaft = new Pen(arrow, Math.Max(1.5f, size * 0.09f))
        {
            EndCap = LineCap.Flat,
            StartCap = LineCap.Flat
        };

        foreach (OneWayPassage passage in _passages)
        {
            if (_map is null || !_map.InBounds(passage.Position)) continue;

            Cell step = Directions.Step(passage.Direction);
            float cx = originX + (passage.Position.X + 0.5f) * size;
            float cy = originY + (passage.Position.Y + 0.5f) * size;

            float headX = cx + step.X * size * 0.30f;
            float headY = cy + step.Y * size * 0.30f;
            float tailX = cx - step.X * size * 0.30f;
            float tailY = cy - step.Y * size * 0.30f;

            graphics.DrawLine(shaft, tailX, tailY, headX, headY);

            // Two barbs behind the head complete the arrowhead.
            float perpX = -step.Y, perpY = step.X;
            float barb = size * 0.16f;
            var a = new PointF(
                headX - step.X * barb + perpX * barb,
                headY - step.Y * barb + perpY * barb);
            var b = new PointF(
                headX - step.X * barb - perpX * barb,
                headY - step.Y * barb - perpY * barb);

            graphics.DrawLine(shaft, a.X, a.Y, headX, headY);
            graphics.DrawLine(shaft, b.X, b.Y, headX, headY);
        }
    }

    /// <summary>
    /// Launchers are arrows too, but green and labelled with the transfer distance:
    /// orange stays "one-way passage", green stays "optional jump".
    /// </summary>
    private void DrawLaunchers(Graphics graphics, float size, float originX, float originY)
    {
        if (_launchers.Count == 0) return;

        using var font = new Font("Consolas", Math.Max(6f, size * 0.20f), FontStyle.Bold);
        using var label = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near
        };

        foreach (DirectionalLauncher launcher in _launchers)
        {
            if (_map is null || !_map.InBounds(launcher.Position)) continue;

            Color tint = launcher.Enabled
                ? Color.FromArgb(47, 107, 79)
                : Color.FromArgb(170, 170, 170);

            Cell step = Directions.Step(launcher.Direction);
            float cx = originX + (launcher.Position.X + 0.5f) * size;
            float cy = originY + (launcher.Position.Y + 0.5f) * size;

            float headX = cx + step.X * size * 0.30f;
            float headY = cy + step.Y * size * 0.30f;
            float tailX = cx - step.X * size * 0.30f;
            float tailY = cy - step.Y * size * 0.30f;

            using var shaft = new Pen(tint, Math.Max(1.5f, size * 0.09f))
            {
                EndCap = LineCap.Flat,
                StartCap = LineCap.Flat
            };
            using var ink = new SolidBrush(tint);

            graphics.DrawLine(shaft, tailX, tailY, headX, headY);

            float perpX = -step.Y, perpY = step.X;
            float barb = size * 0.16f;
            graphics.DrawLine(shaft,
                headX - step.X * barb + perpX * barb, headY - step.Y * barb + perpY * barb, headX, headY);
            graphics.DrawLine(shaft,
                headX - step.X * barb - perpX * barb, headY - step.Y * barb - perpY * barb, headX, headY);

            float inset = Math.Max(1.5f, size * 0.06f);
            var box = new RectangleF(
                originX + launcher.Position.X * size + inset,
                originY + launcher.Position.Y * size + inset,
                size * 0.55f, size * 0.3f);
            graphics.DrawString(launcher.TransferDistance.ToString(), font, ink, box, label);
        }
    }

    /// <summary>Taunt points are a red filled square in the middle of their cell.</summary>
    private void DrawTaunts(Graphics graphics, float size, float originX, float originY)
    {
        if (_tauntCells.Count == 0 || _map is null) return;

        float mark = Math.Max(4f, size * 0.34f);
        using var fill = new SolidBrush(Color.FromArgb(193, 58, 58));

        foreach (Cell cell in _tauntCells)
        {
            if (!_map.InBounds(cell)) continue;

            float cx = originX + (cell.X + 0.5f) * size;
            float cy = originY + (cell.Y + 0.5f) * size;
            graphics.FillRectangle(fill, cx - mark / 2f, cy - mark / 2f, mark, mark);
        }
    }

    private void DrawBadge(Graphics graphics, Cell? cell, string label, float size, float originX, float originY,
        bool square = false)
    {
        if (_map is null || cell is not { } badge) return;
        if (!_map.InBounds(badge)) return;

        // Corner badge: keeps the S/E letters at the cell centre readable.
        float radius = Math.Max(4f, size * 0.19f);
        float cx = originX + (badge.X + 0.26f) * size;
        float cy = originY + (badge.Y + 0.26f) * size;

        using var fill = new SolidBrush(Paper);
        using var ring = new Pen(Ink, Math.Max(1f, size * 0.045f));
        using var font = new Font("Consolas", Math.Max(6f, size * 0.22f), FontStyle.Bold);
        using var ink = new SolidBrush(Ink);
        using var centered = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };

        var bounds = new RectangleF(cx - radius, cy - radius, radius * 2, radius * 2);

        // Circle = map marker (reference / turn cell), square = facility (portal).
        if (square)
        {
            graphics.FillRectangle(fill, bounds);
            graphics.DrawRectangle(ring, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }
        else
        {
            graphics.FillEllipse(fill, bounds);
            graphics.DrawEllipse(ring, bounds);
        }

        graphics.DrawString(label, font, ink, bounds, centered);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_map is null) return;

        // Picking a reference is a tool of its own: while it is on, no paint action runs,
        // for the left and the right button alike.
        if (_pickReference)
        {
            // A wall is a legal reference: the search starts from its open neighbours.
            if (e.Button == MouseButtons.Left && TryCellAt(e.Location, out Cell picked))
                ReferencePicked?.Invoke(this, picked);
            return;
        }

        _lastPainted = null;
        ApplyAt(e.Location, e.Button == MouseButtons.Right);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pickReference) return;
        if ((e.Button & (MouseButtons.Left | MouseButtons.Right)) == 0) return;
        ApplyAt(e.Location, (e.Button & MouseButtons.Right) != 0);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _lastPainted = null;
    }

    private bool TryCellAt(Point location, out Cell cell)
    {
        cell = default;
        if (_map is null) return false;

        (float size, float originX, float originY) = Geometry();
        int x = (int)Math.Floor((location.X - originX) / size);
        int y = (int)Math.Floor((location.Y - originY) / size);
        cell = new Cell(x, y);
        return _map.InBounds(cell);
    }

    private void ApplyAt(Point location, bool erase)
    {
        if (_map is null) return;

        if (!TryCellAt(location, out Cell cell)) return;
        if (_lastPainted == cell) return;
        _lastPainted = cell;

        if (Mode == PaintMode.Portal)
        {
            PortalEdited?.Invoke(this, new FacilityEdit(cell, erase));
            return;
        }

        if (Mode == PaintMode.OneWay)
        {
            OneWayEdited?.Invoke(this, new FacilityEdit(cell, erase));
            return;
        }

        if (Mode == PaintMode.Launcher)
        {
            LauncherEdited?.Invoke(this, new FacilityEdit(cell, erase));
            return;
        }

        if (Mode == PaintMode.Taunt)
        {
            TauntEdited?.Invoke(this, new FacilityEdit(cell, erase));
            return;
        }

        int target;
        if (erase || Mode == PaintMode.Clear)
            target = GridMap.MinCost;
        else if (Mode == PaintMode.Wall)
            target = GridMap.Wall;
        else
            target = Math.Clamp(BrushCost, GridMap.MinCost, GridMap.MaxCost);

        int before = _map.CostAt(cell);
        _map.SetCost(cell, target);

        if (_map.CostAt(cell) != before)
        {
            MapEdited?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
    }

    private (float Size, float OriginX, float OriginY) Geometry()
    {
        if (_map is null) return (0f, 0f, 0f);

        const float padding = 24f;
        float availableWidth = Math.Max(1f, ClientSize.Width - padding * 2);
        float availableHeight = Math.Max(1f, ClientSize.Height - padding * 2);
        float size = Math.Max(2f, Math.Min(availableWidth / _map.Width, availableHeight / _map.Height));

        float width = size * _map.Width;
        float height = size * _map.Height;
        return (size, (ClientSize.Width - width) / 2f, (ClientSize.Height - height) / 2f);
    }

    private static RectangleF CellRect(Cell cell, float size, float originX, float originY)
        => new(originX + cell.X * size, originY + cell.Y * size, size, size);
}
