using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Serialization;
using MPI;
using static ChessEngine.Engine.Search;

namespace ChessEngine.Engine
{
    [Serializable]
    public class InterProcessData
    {
        public List<Board> pos = new List<Board>();
        public int depth;
        public List<Position> pvLine = new List<Position>();
        public List<OpeningMove> GameBook = new List<OpeningMove>();
        public Board ExamineBoard;
        public bool ShallQuit = false;
    }
}