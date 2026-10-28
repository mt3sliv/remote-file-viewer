Attribute VB_Name = "ExcelExample"
Option Explicit

' Uses only late binding: no VBA reference to DAO or the .NET library is needed.
Public Sub QueryDaoIntoExcel()
    Const dbOpenSnapshot As Long = 4
    Dim bridge As Object
    Dim db As Object
    Dim rs As Object
    Dim ws As Worksheet
    Dim i As Long

    On Error GoTo CleanFail

    Set bridge = CreateObject("ExcelDaoBridge.Factory")
    Set db = bridge.OpenOrCreateSample()  ' A real, already OPEN DAO.Database COM object.
    Set rs = db.OpenRecordset("SELECT [Id], [FullName], [City] FROM [People] ORDER BY [Id]", dbOpenSnapshot)

    Set ws = ThisWorkbook.Worksheets(1)
    ws.Cells.ClearContents
    For i = 0 To rs.Fields.Count - 1
        ws.Cells(1, i + 1).Value = rs.Fields(i).Name
    Next i
    If Not (rs.BOF And rs.EOF) Then
        ws.Range("A2").CopyFromRecordset rs
    End If
    ws.Columns("A:C").AutoFit

CleanExit:
    On Error Resume Next
    If Not rs Is Nothing Then rs.Close
    If Not db Is Nothing Then db.Close
    Set rs = Nothing
    Set db = Nothing
    Set bridge = Nothing
    Exit Sub
CleanFail:
    MsgBox "DAO query failed (" & Err.Number & "): " & Err.Description, vbExclamation
    Resume CleanExit
End Sub

Public Sub QueryExistingDatabase()
    Dim bridge As Object, db As Object, rs As Object
    On Error GoTo Failed
    Set bridge = CreateObject("ExcelDaoBridge.Factory")
    Set db = bridge.OpenDatabase("C:\Data\mydata.accdb")
    Set rs = db.OpenRecordset("SELECT TOP 10 * FROM [MyTable]")
    If Not rs.EOF Then Debug.Print rs.Fields(0).Value
Done:
    On Error Resume Next
    If Not rs Is Nothing Then rs.Close
    If Not db Is Nothing Then db.Close
    Exit Sub
Failed:
    MsgBox Err.Description, vbExclamation
    Resume Done
End Sub
